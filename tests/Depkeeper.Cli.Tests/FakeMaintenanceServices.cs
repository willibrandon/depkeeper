namespace Depkeeper.Cli.Tests;

/// <summary>
/// Records controller operations and simulates GitHub revisions and repair outcomes.
/// </summary>
internal sealed class FakeMaintenanceServices : IGitHubGateway, IRepairer
{
    /// <summary>
    /// Gets the current synthetic pull requests.
    /// </summary>
    internal List<PullRequestSnapshot> PullRequests { get; } = [];

    /// <summary>
    /// Gets or sets an optional refresh response override.
    /// </summary>
    internal Func<PullRequestSnapshot, int, PullRequestSnapshot>? OnRefresh { get; set; }

    /// <summary>
    /// Gets or sets the repair callback.
    /// </summary>
    internal Func<PullRequestSnapshot, RepairResult>? OnRepair { get; set; }

    /// <summary>
    /// Gets or sets whether GitHub accepts a merge.
    /// </summary>
    internal bool AcceptMerge { get; set; } = true;

    /// <summary>
    /// Gets the number of attempted merges.
    /// </summary>
    internal int Merges { get; private set; }

    /// <summary>
    /// Gets the number of invoked repair sessions.
    /// </summary>
    internal int Repairs { get; private set; }

    /// <summary>
    /// Gets the number of comments published.
    /// </summary>
    internal int Comments { get; private set; }

    /// <summary>
    /// Gets the number of refresh requests.
    /// </summary>
    internal int Refreshes { get; private set; }

    /// <summary>
    /// Gets the evidence supplied to the most recent repair attempt.
    /// </summary>
    internal string? LastRepairLogs { get; private set; }

    /// <summary>
    /// Gets the exact merge commit checked after a merge.
    /// </summary>
    internal List<string> VerifiedCommits { get; } = [];

    /// <summary>
    /// Gets or sets post-merge verification results.
    /// </summary>
    internal Func<string, IReadOnlyList<CheckSnapshot>>? OnCommitChecks { get; set; }

    /// <summary>
    /// Gets or sets a confirmed descendant containing a follow-up fix.
    /// </summary>
    internal string? RecoveryCommit { get; set; }

    Task<IReadOnlyList<PullRequestSnapshot>> IGitHubGateway.ListAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PullRequestSnapshot>>(PullRequests.Where(pr => pr.Repository == repository && pr.State == "OPEN")
            .ToArray());

    Task<PullRequestSnapshot> IGitHubGateway.RefreshAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        Refreshes++;
        var current = PullRequests.Single(pr => pr.Key == pullRequest.Key);
        return Task.FromResult(OnRefresh?.Invoke(current, Refreshes) ?? current);
    }

    Task<string> IGitHubGateway.GetFailureLogsAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult("Synthetic test failure.");

    Task<bool> IGitHubGateway.UpdateBranchAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<string?> IGitHubGateway.MergeAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        Merges++;
        if (!AcceptMerge) return Task.FromResult<string?>(null);
        var index = PullRequests.FindIndex(pr => pr.Key == pullRequest.Key);
        PullRequests[index] = pullRequest with { State = "MERGED" };
        return Task.FromResult<string?>(new string('c', 40));
    }

    Task<IReadOnlyList<CheckSnapshot>> IGitHubGateway.GetCommitChecksAsync(string repository, string commit,
        CancellationToken cancellationToken)
    {
        VerifiedCommits.Add(commit);
        return Task.FromResult(OnCommitChecks?.Invoke(commit) ?? TestData.PullRequest().Checks);
    }

    Task<string?> IGitHubGateway.GetBranchDescendantAsync(string repository, string branch, string ancestor,
        CancellationToken cancellationToken) => Task.FromResult(RecoveryCommit);

    Task IGitHubGateway.CommentAsync(PullRequestSnapshot pullRequest, string body, CancellationToken cancellationToken)
    {
        Comments++;
        return Task.CompletedTask;
    }

    Task<RepairResult> IRepairer.RepairAsync(PullRequestSnapshot pullRequest, string logs, RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        Repairs++;
        LastRepairLogs = logs;
        return Task.FromResult(OnRepair?.Invoke(pullRequest) ?? new RepairResult(null, "No repair available."));
    }
}
