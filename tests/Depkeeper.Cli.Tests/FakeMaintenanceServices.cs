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

    /// <summary>
    /// Gets or sets the current base head for recovery tests.
    /// </summary>
    internal string BaseHead { get; set; } = new('c', 40);

    /// <summary>
    /// Gets or sets the independently published recovery branch head.
    /// </summary>
    internal string? PublishedRecoveryHead { get; set; }

    /// <summary>
    /// Gets the number of created recovery PRs.
    /// </summary>
    internal int CreatedRecoveries { get; private set; }

    /// <summary>
    /// Gets the number of requested CI refreshes.
    /// </summary>
    internal int CheckRefreshes { get; private set; }

    /// <summary>
    /// Gets or sets an optional new-branch repair response.
    /// </summary>
    internal Func<RecoveryRequest, RepairResult>? OnRecovery { get; set; }

    /// <summary>
    /// Gets unresolved inline review feedback returned by the fake gateway.
    /// </summary>
    internal List<ReviewThread> ReviewThreads { get; } = [];

    /// <summary>
    /// Gets or sets review feedback returned for a particular server read.
    /// </summary>
    internal Func<int, IReadOnlyList<ReviewThread>>? OnReviewThreads { get; set; }

    /// <summary>
    /// Gets the number of review-thread refreshes.
    /// </summary>
    internal int ReviewReads { get; private set; }

    /// <summary>
    /// Gets the review threads resolved by the controller.
    /// </summary>
    internal List<string> ResolvedThreads { get; } = [];

    /// <summary>
    /// Gets or sets the first CI results on a newly created recovery PR.
    /// </summary>
    internal IReadOnlyList<CheckSnapshot>? RecoveryChecks { get; set; }

    /// <summary>
    /// Gets or sets whether retrying the failed base checks resolves a transient failure.
    /// </summary>
    internal bool FailedRerunSucceeds { get; set; }

    Task<IReadOnlyList<PullRequestSnapshot>> IGitHubGateway.ListAsync(string repository, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PullRequestSnapshot>>(PullRequests.Where(pr =>
            pr.Repository == repository && pr.State == "OPEN" && !pr.ManagedRecovery)
            .ToArray());

    Task<PullRequestSnapshot> IGitHubGateway.RefreshAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        Refreshes++;
        var current = PullRequests.Single(pr => pr.Key == pullRequest.Key);
        return Task.FromResult(OnRefresh?.Invoke(current, Refreshes) ?? current);
    }

    Task<string> IGitHubGateway.GetFailureLogsAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult("Synthetic test failure.");

    Task<IReadOnlyList<ReviewThread>> IGitHubGateway.GetReviewThreadsAsync(PullRequestSnapshot pullRequest,
        CancellationToken cancellationToken)
    {
        ReviewReads++;
        return Task.FromResult(OnReviewThreads?.Invoke(ReviewReads) ?? ReviewThreads.ToArray());
    }

    Task IGitHubGateway.ResolveReviewThreadsAsync(PullRequestSnapshot pullRequest, IReadOnlyList<string> threadIds,
        CancellationToken cancellationToken)
    {
        ResolvedThreads.AddRange(threadIds);
        ReviewThreads.RemoveAll(thread => threadIds.Contains(thread.Id, StringComparer.Ordinal));
        return Task.CompletedTask;
    }

    Task<bool> IGitHubGateway.UpdateBranchAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    Task<string?> IGitHubGateway.MergeAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        Merges++;
        if (!AcceptMerge) return Task.FromResult<string?>(null);
        var index = PullRequests.FindIndex(pr => pr.Key == pullRequest.Key);
        var merged = new string(pullRequest.ManagedRecovery ? 'e' : 'c', 40);
        PullRequests[index] = pullRequest with { State = "MERGED", MergeCommit = merged };
        BaseHead = merged;
        return Task.FromResult<string?>(merged);
    }

    Task<IReadOnlyList<CheckSnapshot>> IGitHubGateway.GetCommitChecksAsync(string repository, string commit,
        CancellationToken cancellationToken)
    {
        VerifiedCommits.Add(commit);
        return Task.FromResult(OnCommitChecks?.Invoke(commit) ?? TestData.PullRequest().Checks);
    }

    Task<string?> IGitHubGateway.GetBranchDescendantAsync(string repository, string branch, string ancestor,
        CancellationToken cancellationToken) => Task.FromResult(RecoveryCommit);

    Task<string> IGitHubGateway.GetBranchHeadAsync(string repository, string branch, CancellationToken cancellationToken) =>
        Task.FromResult(branch.StartsWith("depkeeper/", StringComparison.Ordinal) ? PublishedRecoveryHead ?? "" : BaseHead);

    Task<bool> IGitHubGateway.RerunChecksAsync(string repository, string commit, IReadOnlyList<CheckSnapshot> checks, bool failedOnly,
        CancellationToken cancellationToken)
    {
        CheckRefreshes++;
        if (failedOnly)
        {
            if (FailedRerunSucceeds) OnCommitChecks = _ => TestData.PullRequest().Checks;
            return Task.FromResult(FailedRerunSucceeds);
        }
        for (var index = 0; index < PullRequests.Count; index++)
            PullRequests[index] = PullRequests[index] with
            {
                Checks = PullRequests[index].Checks.Select(check => check with { CompletedAt = DateTimeOffset.UtcNow }).ToArray()
            };
        return Task.FromResult(true);
    }

    Task<PullRequestSnapshot?> IGitHubGateway.FindRecoveryAsync(RecoveryRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(PullRequests.SingleOrDefault(pr => pr.Repository == request.Repository && pr.Branch == request.Branch));

    Task<PullRequestSnapshot> IGitHubGateway.CreateRecoveryPullRequestAsync(RecoveryRequest request, RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        CreatedRecoveries++;
        var created = TestData.PullRequest(1001) with
        {
            Author = "maintainer",
            Branch = request.Branch,
            Head = PublishedRecoveryHead!,
            ManagedRecovery = true,
            Checks = RecoveryChecks ?? TestData.PullRequest().Checks
        };
        PullRequests.Add(created);
        return Task.FromResult(created);
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
        LastRepairLogs = logs;
        return Task.FromResult(OnRepair?.Invoke(pullRequest) ?? new RepairResult(null, "No repair available."));
    }

    Task<RepairResult> IRepairer.CreateRecoveryAsync(RecoveryRequest request, string logs, RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        Repairs++;
        LastRepairLogs = logs;
        var result = OnRecovery?.Invoke(request) ?? new RepairResult(new string('d', 40), "Validated recovery.");
        PublishedRecoveryHead = result.Head;
        return Task.FromResult(result);
    }
}
