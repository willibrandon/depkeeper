using System.Globalization;

namespace Depkeeper.Cli;

/// <summary>
/// Tracks verification of exact merge commits until their base-branch checks finish successfully.
/// </summary>
internal sealed class PostMergeVerifier
{
    private readonly IGitHubGateway _github;
    private readonly StateStore _state;
    private readonly Redactor _redactor;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    /// Creates a verifier using the controller's existing state and timing boundaries.
    /// </summary>
    /// <param name="github">The controller-owned GitHub gateway.</param>
    /// <param name="state">The durable checkpoint store.</param>
    /// <param name="redactor">The diagnostic redactor.</param>
    /// <param name="clock">The controller clock.</param>
    /// <param name="pollInterval">The check polling interval.</param>
    internal PostMergeVerifier(IGitHubGateway github, StateStore state, Redactor redactor, TimeProvider clock, TimeSpan pollInterval)
    {
        _github = github;
        _state = state;
        _redactor = redactor;
        _clock = clock;
        _pollInterval = pollInterval;
    }

    /// <summary>
    /// Rechecks merged commits retained from earlier runs even though their PRs are no longer open.
    /// </summary>
    /// <param name="repository">The selected repository.</param>
    /// <param name="profile">The repository's verification policy.</param>
    /// <param name="dryRun">Whether checkpoints are read-only.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>Results for the tracked merged commits.</returns>
    internal async Task<IReadOnlyList<ReportEntry>> RecheckAsync(string repository, RepositoryProfile profile, bool dryRun,
        CancellationToken cancellationToken)
    {
        var entries = new List<ReportEntry>();
        var prefix = repository + "#";
        foreach (var (key, attempt) in _state.State.PullRequests
            .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value.MergeHead is not null).ToArray())
        {
            var number = int.Parse(key.AsSpan(prefix.Length), CultureInfo.InvariantCulture);
            var entry = await VerifyAsync(repository, number, attempt, profile, dryRun, TimeSpan.Zero, cancellationToken);
            if (entry.Outcome != "merged" && attempt.MergeBranch is not null)
            {
                var descendant = await _github.GetBranchDescendantAsync(repository, attempt.MergeBranch, attempt.MergeHead!,
                    cancellationToken);
                if (descendant is not null)
                {
                    var checks = await _github.GetCommitChecksAsync(repository, descendant, cancellationToken);
                    var policy = profile with { RequiredChecks = profile.PostMergeChecks ?? profile.RequiredChecks };
                    if (MergePolicy.GetChecksBlocker(checks, policy) is null)
                    {
                        entry = new ReportEntry(repository, number, "merged",
                            $"Post-merge CI for {attempt.MergeHead} was unresolved; " +
                            $"verified descendant {descendant} now passes CI.");
                        if (!dryRun)
                        {
                            _state.State.PullRequests.Remove(key);
                            _state.Save();
                        }
                    }
                }
            }
            entries.Add(entry);
        }
        return entries;
    }

    /// <summary>
    /// Verifies the merged commit and retains unresolved checks for later sweeps.
    /// </summary>
    /// <param name="repository">The affected repository.</param>
    /// <param name="number">The merged PR number.</param>
    /// <param name="attempt">The checkpoint containing the exact merge commit.</param>
    /// <param name="profile">The repository's verification policy.</param>
    /// <param name="dryRun">Whether checkpoints are read-only.</param>
    /// <param name="wait">The maximum time to wait for new checks.</param>
    /// <param name="cancellationToken">Cancels polling.</param>
    /// <returns>A verified merge, pending checks, or a post-merge blocker.</returns>
    internal async Task<ReportEntry> VerifyAsync(string repository, int number, AttemptState attempt, RepositoryProfile profile,
        bool dryRun, TimeSpan wait, CancellationToken cancellationToken)
    {
        var head = attempt.MergeHead ?? throw new InvalidOperationException("A merge commit is required for post-merge verification.");
        var key = repository + "#" + number.ToString(CultureInfo.InvariantCulture);
        var checkPolicy = profile with { RequiredChecks = profile.PostMergeChecks ?? profile.RequiredChecks };
        var deadline = _clock.GetUtcNow() + wait;
        ReportEntry entry;
        try
        {
            IReadOnlyList<CheckSnapshot> checks;
            string? reason;
            bool failed;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait < _pollInterval ? wait : _pollInterval, _clock, cancellationToken);
            while (true)
            {
                checks = await _github.GetCommitChecksAsync(repository, head, cancellationToken);
                reason = MergePolicy.GetChecksBlocker(checks, checkPolicy);
                failed = checks.Any(check => check.Failed &&
                    !(profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal));
                if (reason is null || failed && checks.All(check => check.Finished) || _clock.GetUtcNow() >= deadline) break;
                var remaining = deadline - _clock.GetUtcNow();
                if (remaining <= TimeSpan.Zero) break;
                await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _clock, cancellationToken);
            }
            if (failed) reason = "Failed checks: " + string.Join(", ", checks.Where(check => check.Failed &&
                !(profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal)).Select(check => check.Name));
            var outcome = reason is null ? "merged" : failed ? "blocked" :
                checks.Count == 0 || checks.Any(check => !check.Finished) ? "pending" : "blocked";
            var subject = attempt.Recovery?.PullRequest is int recovery ? $"Recovery PR #{recovery} merged as {head}" : $"Merged as {head}";
            var detail = reason is null ? $"{subject}; post-merge CI passed." : $"{subject}; post-merge verification: {reason}";
            entry = new ReportEntry(repository, number, outcome, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (FailurePolicy.CanReport(exception))
        {
            entry = new ReportEntry(repository, number, "blocked", $"Merged as {head}; could not verify post-merge CI: " +
                _redactor.Clean(exception.Message));
        }
        var cleanDetail = _redactor.Clean(entry.Detail);
        entry = entry with { Detail = cleanDetail.Length > 1500 ? cleanDetail[..1500] : cleanDetail };
        if (!dryRun)
        {
            if (entry.Outcome == "merged")
            {
                _state.State.PullRequests.Remove(key);
                _state.Save();
            }
            else _state.Set(key, attempt with
            {
                Blocked = entry.Outcome == "blocked",
                Reason = entry.Detail,
                UpdatedAt = _clock.GetUtcNow()
            });
        }
        return entry;
    }
}
