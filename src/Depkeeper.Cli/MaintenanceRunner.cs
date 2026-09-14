namespace Depkeeper.Cli;

/// <summary>
/// Coordinates discovery, bounded repairs, fresh CI checks, and exact-head merges.
/// </summary>
internal sealed class MaintenanceRunner
{
    private readonly IGitHubGateway _github;
    private readonly IRepairer _repairer;
    private readonly StateStore _state;
    private readonly Redactor _redactor;
    private readonly TimeProvider _clock;
    private readonly IReleaseAgeGate _releaseAge;
    private readonly TimeSpan _pollInterval;
    private readonly PostMergeVerifier _postMerge;

    /// <summary>
    /// Creates a controller with replaceable GitHub and repair boundaries.
    /// </summary>
    /// <param name="github">The GitHub gateway.</param>
    /// <param name="repairer">The repair implementation.</param>
    /// <param name="state">The durable checkpoint store.</param>
    /// <param name="redactor">The report redactor.</param>
    /// <param name="releaseAge">The publication-age gate.</param>
    /// <param name="clock">An optional test clock.</param>
    /// <param name="pollInterval">An optional polling interval for deterministic tests.</param>
    internal MaintenanceRunner(IGitHubGateway github, IRepairer repairer, StateStore state, Redactor redactor,
        IReleaseAgeGate releaseAge, TimeProvider? clock = null, TimeSpan? pollInterval = null)
    {
        _github = github;
        _repairer = repairer;
        _state = state;
        _redactor = redactor;
        _clock = clock ?? TimeProvider.System;
        _releaseAge = releaseAge;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
        _postMerge = new PostMergeVerifier(github, state, redactor, _clock, _pollInterval);
    }

    /// <summary>
    /// Runs a maintenance sweep while containing failures to their repository or PR.
    /// </summary>
    /// <param name="settings">The run policy.</param>
    /// <param name="cancellationToken">Cancels the sweep.</param>
    /// <returns>Verified outcomes and unresolved blockers.</returns>
    internal async Task<IReadOnlyList<ReportEntry>> RunAsync(RunSettings settings, CancellationToken cancellationToken)
    {
        var results = new List<ReportEntry>();
        var repairs = 0;
        var startIndex = Math.Abs(_state.State.NextRepository % settings.Repositories.Length);
        for (var offset = 0; offset < settings.Repositories.Length; offset++)
        {
            var repositoryIndex = (startIndex + offset) % settings.Repositories.Length;
            var repository = settings.Repositories[repositoryIndex];
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var profile = settings.Profiles.GetValueOrDefault(repository) ?? new RepositoryProfile();
                var followups = await _postMerge.RecheckAsync(repository, profile, settings.DryRun, cancellationToken);
                results.AddRange(followups);
                var unresolvedMerge = followups.Any(entry => entry.Outcome != "merged");
                var candidates = await _github.ListAsync(repository, cancellationToken);
                var mutated = false;
                foreach (var candidate in candidates.Where(pr => settings.OnlyPullRequest is null || pr.Number == settings.OnlyPullRequest)
                    .OrderBy(pr => MergePolicy.HasFailure(pr, profile)))
                {
                    if (unresolvedMerge || mutated && !settings.DryRun)
                    {
                        results.Add(new ReportEntry(repository, candidate.Number, "deferred",
                            unresolvedMerge ? "Post-merge verification is unresolved for this repository." :
                                "Another PR in this repository was updated; reassess against the new base next run."));
                        continue;
                    }
                    var current = await _github.RefreshAsync(candidate, cancellationToken);
                    if (!MergePolicy.IsEligible(current)) continue;
                    var agePolicy = profile.ReleaseAge ?? settings.ReleaseAge ?? new ReleaseAgePolicy();
                    var ageBlocker = await _releaseAge.GetBlockerAsync(current, agePolicy, cancellationToken);
                    if (ageBlocker is not null)
                    {
                        results.Add(new ReportEntry(repository, current.Number, "cooldown", ageBlocker));
                        continue;
                    }
                    _state.State.PullRequests.TryGetValue(current.Key, out var previous);
                    if (previous?.Head != current.Head) previous = null;
                    if (previous?.Blocked == true && !settings.RetryBlocked)
                    {
                        results.Add(new ReportEntry(repository, current.Number, "blocked", previous.Reason));
                        continue;
                    }
                    var attempts = settings.RetryBlocked ? 0 : previous?.Attempts ?? 0;
                    try
                    {
                        if (current.MergeState == "BEHIND")
                        {
                            if (settings.DryRun)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "would-update",
                                    "Bring the branch up to date and rerun CI."));
                                continue;
                            }
                            if (!await _github.UpdateBranchAsync(current, cancellationToken))
                                throw new InvalidOperationException(
                                    "GitHub could not update the branch; resolve conflicts or permissions.");
                            mutated = true;
                            current = await WaitAsync(current, settings.CiTimeout, true, cancellationToken);
                        }
                        if (settings.DryRun)
                        {
                            var blocker = MergePolicy.GetBlocker(current, current.Head, profile);
                            var outcome = blocker is null ? "would-merge" : MergePolicy.HasFailure(current, profile) ? "would-repair" :
                                MergePolicy.IsPending(current) ? "pending" : "blocked";
                            results.Add(new ReportEntry(repository, current.Number,
                                outcome, blocker ?? "All observed merge gates pass."));
                            continue;
                        }

                        while (MergePolicy.HasFailure(current, profile) && attempts < settings.MaxAttempts && repairs < settings.MaxRepairs)
                        {
                            attempts++;
                            repairs++;
                            mutated = true;
                            _state.SetNextRepository((repositoryIndex + 1) % settings.Repositories.Length);
                            _state.Set(current.Key,
                                new AttemptState(current.Head, attempts, false, "Repair in progress.", _clock.GetUtcNow()));
                            var logs = await _github.GetFailureLogsAsync(current, cancellationToken);
                            if (settings.RetryBlocked && previous?.Blocked == true)
                                logs += "\nPrevious independent verification for this revision:\n" + previous.Reason;
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            timeout.CancelAfter(settings.RepairTimeout);
                            RepairResult repair;
                            try { repair = await _repairer.RepairAsync(current, logs, profile, timeout.Token); }
                            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                            {
                                throw new InvalidOperationException(
                                    "Repair exceeded its time budget. Inspect the failure and retry explicitly.");
                            }
                            if (repair.Head is null) throw new InvalidOperationException(repair.Summary);
                            mutated = true;
                            current = current with { Head = repair.Head };
                            _state.Set(current.Key, new AttemptState(current.Head, attempts, false, "Waiting for CI.", _clock.GetUtcNow()));
                            current = await WaitAsync(current, settings.CiTimeout, false, cancellationToken);
                            if (current.Head != repair.Head)
                                throw new InvalidOperationException(
                                    "The PR changed after repair; inspect the new revision before continuing.");
                        }

                        if (MergePolicy.HasFailure(current, profile))
                        {
                            if (attempts >= settings.MaxAttempts)
                                throw new InvalidOperationException(
                                    "Repair attempt limit reached. Failing checks: " + FailedNames(current));
                            results.Add(new ReportEntry(repository, current.Number, "deferred",
                                "Daily repair budget reached. Failing checks: " + FailedNames(current)));
                            continue;
                        }
                        var verifiedHead = current.Head;
                        current = await _github.RefreshAsync(current, cancellationToken);
                        var finalAgeBlocker = await _releaseAge.GetBlockerAsync(current, agePolicy, cancellationToken);
                        if (finalAgeBlocker is not null)
                        {
                            results.Add(new ReportEntry(repository, current.Number, "cooldown", finalAgeBlocker));
                            continue;
                        }
                        var reason = MergePolicy.GetBlocker(current, verifiedHead, profile);
                        if (reason is not null)
                        {
                            results.Add(new ReportEntry(repository, current.Number,
                                MergePolicy.IsPending(current) ? "pending" : "blocked", reason));
                            continue;
                        }
                        var mergeHead = await _github.MergeAsync(current, cancellationToken) ??
                            throw new InvalidOperationException(
                                "GitHub did not confirm a merge; inspect branch rules, merge queues, and review threads.");
                        mutated = true;
                        var mergedAttempt = new AttemptState(current.Head, attempts, false, "Waiting for post-merge CI.",
                            _clock.GetUtcNow(), mergeHead, current.BaseBranch);
                        _state.Set(current.Key, mergedAttempt);
                        results.Add(await _postMerge.VerifyAsync(repository, current.Number, mergedAttempt, profile, false,
                            settings.CiTimeout, cancellationToken));
                        var advisory = current.Checks.Where(check =>
                            (profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal) &&
                            check.State is not ("SUCCESS" or "NEUTRAL" or "SKIPPED")).Select(check => check.Name).ToArray();
                        if (advisory.Length > 0) results.Add(new ReportEntry(repository, current.Number, "advisory",
                            "Nonblocking checks: " + string.Join(", ", advisory)));
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) when (FailurePolicy.CanReport(exception))
                    {
                        var reason = _redactor.Clean(exception.Message);
                        if (reason.Length > 1500) reason = reason[..1500];
                        results.Add(new ReportEntry(repository, current.Number, "blocked", reason));
                        if (!settings.DryRun)
                        {
                            _state.Set(current.Key, new AttemptState(current.Head, attempts, true, reason, _clock.GetUtcNow()));
                            try { await _github.CommentAsync(current, "Depkeeper stopped this repair: " + reason, cancellationToken); }
                            catch (Exception commentError) when (commentError is IOException or InvalidOperationException)
                            {
                                results.Add(new ReportEntry(repository, current.Number, "advisory",
                                    "The PR comment could not be posted; the blocker is recorded in this report."));
                            }
                        }
                    }
                }
                if (candidates.Count == 0 && followups.Count == 0)
                    results.Add(new ReportEntry(repository, 0, "clear", "No open Dependabot PRs."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (FailurePolicy.CanReport(exception))
            {
                results.Add(new ReportEntry(repository, 0, "blocked", _redactor.Clean(exception.Message)));
            }
        }
        if (!settings.DryRun) _state.Save();
        return results;
    }

    private async Task<PullRequestSnapshot> WaitAsync(PullRequestSnapshot expected, TimeSpan timeout, bool requireNewHead,
        CancellationToken cancellationToken)
    {
        var deadline = _clock.GetUtcNow() + timeout;
        var latest = expected;
        while (_clock.GetUtcNow() < deadline)
        {
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _clock, cancellationToken);
            latest = await _github.RefreshAsync(expected, cancellationToken);
            if (requireNewHead && latest.Head == expected.Head) continue;
            if (!requireNewHead && latest.Head != expected.Head) return latest;
            if (MergePolicy.ChecksFinished(latest) && latest.Mergeable != "UNKNOWN") return latest;
        }
        return latest;
    }

    private static string FailedNames(PullRequestSnapshot pullRequest) => string.Join(", ", pullRequest.Checks
        .Where(check => check.State is "FAILURE" or "ERROR" or "TIMED_OUT" or "CANCELLED" or "ACTION_REQUIRED")
        .Select(check => check.Name));
}
