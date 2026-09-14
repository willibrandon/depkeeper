namespace Depkeeper.Cli;

/// <summary>
/// Refreshes stale check results so old audit successes are not treated as current verification.
/// </summary>
internal sealed class FreshCi
{
    private readonly IGitHubGateway _github;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;

    /// <summary>
    /// Creates a freshness gate using controller-owned GitHub operations.
    /// </summary>
    /// <param name="github">The GitHub gateway.</param>
    /// <param name="clock">The controller clock.</param>
    /// <param name="pollInterval">The polling interval.</param>
    internal FreshCi(IGitHubGateway github, TimeProvider clock, TimeSpan pollInterval)
    {
        _github = github;
        _clock = clock;
        _pollInterval = pollInterval;
    }

    /// <summary>
    /// Detects successful required evidence that is stale or lacks a completion timestamp.
    /// </summary>
    /// <param name="pullRequest">The inspected revision.</param>
    /// <param name="profile">The freshness policy.</param>
    /// <returns>Whether CI must be refreshed before merging.</returns>
    internal bool Needed(PullRequestSnapshot pullRequest, RepositoryProfile profile) => profile.MaximumCheckAgeHours > 0 &&
        pullRequest.Checks.Any(check => IsWorkflowCheck(pullRequest.Repository, check) && check.State == "SUCCESS" &&
            !(profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal) &&
            (check.CompletedAt is null || check.CompletedAt < _clock.GetUtcNow().AddHours(-profile.MaximumCheckAgeHours)));

    /// <summary>
    /// Reruns stale workflows and waits for fresh evidence on the same PR head.
    /// </summary>
    /// <param name="expected">The exact inspected head.</param>
    /// <param name="profile">The repository's check policy.</param>
    /// <param name="timeout">The CI time budget.</param>
    /// <param name="cancellationToken">Cancels refresh and polling.</param>
    /// <returns>The latest check snapshot.</returns>
    internal async Task<PullRequestSnapshot> EnsureAsync(PullRequestSnapshot expected, RepositoryProfile profile,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Needed(expected, profile)) return expected;
        var requested = _clock.GetUtcNow().AddSeconds(-5);
        if (!await _github.RerunChecksAsync(expected.Repository, expected.Head, expected.Checks, false, cancellationToken))
            throw new IOException("Stale CI could not be refreshed; the PR remains unmerged.");
        var deadline = _clock.GetUtcNow() + timeout;
        var latest = expected;
        while (_clock.GetUtcNow() < deadline)
        {
            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < _pollInterval ? remaining : _pollInterval, _clock, cancellationToken);
            latest = await _github.RefreshAsync(expected, cancellationToken);
            if (latest.Head != expected.Head) return latest;
            if (MergePolicy.ChecksFinished(latest) && (MergePolicy.HasFailure(latest, profile) ||
                latest.Checks.Where(check => IsWorkflowCheck(latest.Repository, check) && check.State == "SUCCESS")
                    .All(check => check.CompletedAt >= requested))) return latest;
        }
        if (MergePolicy.ChecksFinished(latest) && Needed(latest, profile))
            throw new IOException("Fresh CI results were not confirmed before the time limit.");
        return latest;
    }

    private static bool IsWorkflowCheck(string repository, CheckSnapshot check) =>
        Uri.TryCreate(check.Url, UriKind.Absolute, out var url) && url.Scheme == "https" && url.Host == "github.com" &&
        url.AbsolutePath.StartsWith("/" + repository + "/actions/runs/", StringComparison.OrdinalIgnoreCase);
}
