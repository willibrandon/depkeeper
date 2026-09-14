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

    Task<IReadOnlyList<PullRequestSnapshot>> IGitHubGateway.ListAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PullRequestSnapshot>>(PullRequests.Where(pr => pr.Repository == repository).ToArray());

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

    Task<bool> IGitHubGateway.MergeAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        Merges++;
        return Task.FromResult(AcceptMerge);
    }

    Task IGitHubGateway.CommentAsync(PullRequestSnapshot pullRequest, string body, CancellationToken cancellationToken)
    {
        Comments++;
        return Task.CompletedTask;
    }

    Task<RepairResult> IRepairer.RepairAsync(PullRequestSnapshot pullRequest, string logs, RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        Repairs++;
        return Task.FromResult(OnRepair?.Invoke(pullRequest) ?? new RepairResult(null, "No repair available."));
    }
}
