using System.Globalization;

namespace Depkeeper.Cli;

/// <summary>
/// Repairs persistent post-merge failures through one verified, controller-owned follow-up PR.
/// </summary>
internal sealed class RecoveryRunner
{
    private readonly IGitHubGateway _github;
    private readonly IRepairer _repairer;
    private readonly StateStore _state;
    private readonly Redactor _redactor;
    private readonly IReleaseAgeGate _releaseAge;
    private readonly PostMergeVerifier _postMerge;
    private readonly FreshCi _freshCi;
    private readonly ReviewCoordinator _reviews;
    private readonly TimeProvider _clock;
    private readonly Func<PullRequestSnapshot, TimeSpan, bool, CancellationToken, Task<PullRequestSnapshot>> _wait;

    /// <summary>
    /// Creates a recovery controller sharing the normal verification and persistence boundaries.
    /// </summary>
    /// <param name="github">The GitHub gateway.</param>
    /// <param name="repairer">The SDK-backed repair worker.</param>
    /// <param name="state">The durable checkpoint store.</param>
    /// <param name="redactor">The diagnostic redactor.</param>
    /// <param name="releaseAge">The publication-age policy.</param>
    /// <param name="postMerge">The merged-commit verifier.</param>
    /// <param name="freshCi">The check freshness gate.</param>
    /// <param name="clock">The controller clock.</param>
    /// <param name="wait">The existing exact-head CI polling operation.</param>
    internal RecoveryRunner(IGitHubGateway github, IRepairer repairer, StateStore state, Redactor redactor,
        IReleaseAgeGate releaseAge, PostMergeVerifier postMerge, FreshCi freshCi, TimeProvider clock,
        Func<PullRequestSnapshot, TimeSpan, bool, CancellationToken, Task<PullRequestSnapshot>> wait)
    {
        _github = github;
        _repairer = repairer;
        _state = state;
        _redactor = redactor;
        _releaseAge = releaseAge;
        _postMerge = postMerge;
        _freshCi = freshCi;
        _reviews = new ReviewCoordinator(github, redactor);
        _clock = clock;
        _wait = wait;
    }

    /// <summary>
    /// Advances a failed merge's existing recovery or creates its single bounded repair PR.
    /// </summary>
    /// <param name="repository">The affected repository.</param>
    /// <param name="number">The original dependency PR number.</param>
    /// <param name="saved">The persisted failed-merge state.</param>
    /// <param name="profile">Trusted repository policy.</param>
    /// <param name="settings">The run limits.</param>
    /// <param name="budget">Remaining repair sessions in this sweep.</param>
    /// <param name="cancellationToken">Cancels all recovery operations.</param>
    /// <returns>The source PR's outcome, consumed repair sessions, and whether recovery work was attempted.</returns>
    internal async Task<(ReportEntry Entry, int Repairs, bool Mutated)> RunAsync(string repository, int number,
        AttemptState saved, RepositoryProfile profile, RunSettings settings, int budget, CancellationToken cancellationToken)
    {
        var key = repository + "#" + number.ToString(CultureInfo.InvariantCulture);
        var attempt = saved;
        var recovery = saved.Recovery;
        var used = 0;
        var mutated = false;
        try
        {
            var source = await _github.RefreshAsync(PullRequestSnapshot.Lookup(repository, number), cancellationToken);
            if (source.State != "MERGED" || source.Author is not ("app/dependabot" or "dependabot[bot]"))
                throw new InvalidOperationException("Automatic recovery requires a confirmed merged Dependabot PR.");
            var branch = saved.MergeBranch ?? source.BaseBranch;
            var baseHead = await _github.GetBranchHeadAsync(repository, branch, cancellationToken);
            recovery ??= new RecoveryState("depkeeper/repair-" + saved.MergeHead, baseHead);
            if (settings.RetryBlocked && recovery.Blocked)
                recovery = recovery with { Attempts = 0, Blocked = false };
            var request = new RecoveryRequest(repository, number, branch, recovery.BaseHead, recovery.Branch);
            var pullRequest = await _github.FindRecoveryAsync(request, cancellationToken);
            if (pullRequest is null)
            {
                if (recovery.Head is null)
                {
                    if (recovery.Blocked) throw new InvalidOperationException(recovery.Reason ?? "Recovery is blocked.");
                    if (baseHead != saved.MergeHead &&
                        await _github.GetBranchDescendantAsync(repository, branch, saved.MergeHead!, cancellationToken) != baseHead)
                        throw new InvalidOperationException("The base no longer matches the failed merge's ancestry.");
                    var checks = await _github.GetCommitChecksAsync(repository, baseHead, cancellationToken);
                    if (checks.Any(check => !check.Finished))
                        return (Report("pending", "Waiting for the current base checks before attempting recovery."), used, mutated);
                    if (MergePolicy.GetChecksBlocker(checks,
                        profile with { RequiredChecks = profile.PostMergeChecks ?? profile.RequiredChecks }) is null)
                        return (await _postMerge.VerifyAsync(repository, number, attempt with { MergeHead = baseHead }, profile,
                            false, TimeSpan.Zero, cancellationToken), used, mutated);
                    if (!checks.Any(check => check.Failed))
                        return (Report("blocked", "Required post-merge verification is missing; a code repair cannot supply it."),
                            used, mutated);
                    if (!recovery.CiRetried)
                    {
                        recovery = recovery with { CiRetried = true, BaseHead = baseHead };
                        Save();
                        if (await _github.RerunChecksAsync(repository, baseHead, checks, true, cancellationToken))
                        {
                            var retried = await _postMerge.VerifyAsync(repository, number,
                                attempt with { MergeHead = baseHead }, profile, false, settings.CiTimeout, cancellationToken);
                            if (retried.Outcome != "blocked") return (retried, used, mutated);
                            attempt = _state.State.PullRequests[key];
                            checks = await _github.GetCommitChecksAsync(repository, baseHead, cancellationToken);
                        }
                    }
                    if (budget <= used)
                        return (Report("deferred", "Post-merge recovery is waiting for the next repair budget."), used, mutated);
                    if (recovery.Attempts >= settings.MaxAttempts) throw new InvalidOperationException("Recovery attempt limit reached.");
                    recovery = recovery with { BaseHead = baseHead, Attempts = recovery.Attempts + 1 };
                    used++;
                    mutated = true;
                    Save();
                    var logs = await _github.GetFailureLogsAsync(source with { Head = baseHead, Checks = checks }, cancellationToken);
                    logs += "\nPost-merge verification:\n" + saved.Reason + "\nPrevious recovery:\n" + recovery.Reason;
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(settings.RepairTimeout);
                    request = request with { BaseHead = baseHead };
                    var result = await _repairer.CreateRecoveryAsync(request, logs, profile, timeout.Token);
                    if (result.Head is null) throw new InvalidOperationException(result.Summary);
                    recovery = recovery with { Head = result.Head };
                    Save();
                }
                if (await _github.GetBranchHeadAsync(repository, recovery.Branch, cancellationToken) != recovery.Head)
                    throw new InvalidOperationException("The published recovery branch changed before PR creation.");
                pullRequest = await _github.CreateRecoveryPullRequestAsync(request, profile, cancellationToken);
                mutated = true;
            }
            recovery = recovery with { PullRequest = pullRequest.Number, Head = pullRequest.Head };
            Save();
            if (!pullRequest.ManagedRecovery) throw new InvalidOperationException("The recovery PR's ownership was not verified.");
            if (pullRequest.State == "MERGED")
            {
                var merged = pullRequest.MergeCommit ?? throw new InvalidOperationException("The recovery merge commit is unavailable.");
                attempt = attempt with { MergeHead = merged };
                Save();
                return (await _postMerge.VerifyAsync(repository, number, attempt, profile, false,
                    settings.CiTimeout, cancellationToken), used, mutated);
            }
            if (pullRequest.State != "OPEN") throw new InvalidOperationException("The existing recovery PR was closed without merging.");
            if (pullRequest.Draft) return (Report("pending", $"Recovery PR #{pullRequest.Number} is a draft."), used, mutated);
            if (pullRequest.MergeState == "BEHIND")
            {
                if (!await _github.UpdateBranchAsync(pullRequest, cancellationToken))
                    throw new InvalidOperationException("The recovery branch could not be updated from its base.");
                mutated = true;
                pullRequest = await _wait(pullRequest, settings.CiTimeout, true, cancellationToken);
            }
            if (!MergePolicy.ChecksFinished(pullRequest) || pullRequest.Mergeable == "UNKNOWN")
                pullRequest = await _wait(pullRequest, settings.CiTimeout, false, cancellationToken);
            var expectedHead = pullRequest.Head;
            pullRequest = await _freshCi.EnsureAsync(pullRequest, profile, settings.CiTimeout, cancellationToken);
            if (pullRequest.Head != expectedHead) throw new InvalidOperationException("The recovery PR changed during verification.");
            var reviewThreads = await _reviews.GetAsync(pullRequest, cancellationToken);
            while (MergePolicy.HasFailure(pullRequest, profile) || reviewThreads.Count > 0)
            {
                if (recovery.Blocked || recovery.Attempts >= settings.MaxAttempts)
                    throw new InvalidOperationException(recovery.Reason ?? "Recovery attempt limit reached.");
                if (used >= budget) return (Report("deferred", $"Recovery PR #{pullRequest.Number} is waiting for repair capacity."),
                    used, mutated);
                recovery = recovery with { Attempts = recovery.Attempts + 1 };
                used++;
                mutated = true;
                Save();
                var logs = await _github.GetFailureLogsAsync(pullRequest, cancellationToken);
                logs += _reviews.Format(reviewThreads);
                logs += "\nPrevious recovery verification:\n" + recovery.Reason;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.RepairTimeout);
                var repaired = await _repairer.RepairAsync(pullRequest, logs, profile, timeout.Token);
                if (repaired.Head is null) throw new InvalidOperationException(repaired.Summary);
                recovery = recovery with { Head = repaired.Head };
                Save();
                pullRequest = await _wait(pullRequest with { Head = repaired.Head }, settings.CiTimeout, false, cancellationToken);
                if (pullRequest.Head != repaired.Head) throw new InvalidOperationException("The recovery PR changed after repair.");
                reviewThreads = await _reviews.ResolveAddressedAsync(pullRequest, reviewThreads,
                    repaired.ChangedPaths ?? [], cancellationToken);
            }
            expectedHead = pullRequest.Head;
            pullRequest = await _github.RefreshAsync(pullRequest, cancellationToken);
            reviewThreads = await _reviews.GetAsync(pullRequest, cancellationToken);
            if (reviewThreads.Count > 0)
                return (Report("blocked", $"Recovery PR #{pullRequest.Number} has unresolved review feedback."), used, mutated);
            var blocker = MergePolicy.GetBlocker(pullRequest, expectedHead, profile);
            if (blocker is not null) return (Report(MergePolicy.IsPending(pullRequest) ? "pending" : "blocked",
                $"Recovery PR #{pullRequest.Number}: {blocker}"), used, mutated);
            var age = await _releaseAge.GetBlockerAsync(pullRequest, profile.ReleaseAge ?? settings.ReleaseAge ?? new ReleaseAgePolicy(),
                cancellationToken);
            if (age is not null) return (Report("cooldown", $"Recovery PR #{pullRequest.Number}: {age}"), used, mutated);
            var mergeHead = await _github.MergeAsync(pullRequest, cancellationToken) ??
                throw new InvalidOperationException("GitHub did not confirm the recovery PR merge.");
            mutated = true;
            attempt = attempt with { MergeHead = mergeHead, MergeBranch = branch };
            Save();
            return (await _postMerge.VerifyAsync(repository, number, attempt, profile, false,
                settings.CiTimeout, cancellationToken), used, mutated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (FailurePolicy.CanReport(exception))
        {
            var reason = exception is OperationCanceledException ? "Recovery exceeded its time budget." :
                _redactor.Clean(exception.Message);
            if (reason.Length > 1500) reason = reason[..1500];
            if (recovery is not null) recovery = recovery with { Blocked = true, Reason = reason };
            attempt = attempt with { Blocked = true, Reason = reason };
            Save();
            return (Report("blocked", reason), used, mutated);
        }

        void Save()
        {
            attempt = attempt with { Recovery = recovery, UpdatedAt = _clock.GetUtcNow() };
            _state.Set(key, attempt);
        }

        ReportEntry Report(string outcome, string detail) => new(repository, number, outcome, _redactor.Clean(detail));
    }
}
