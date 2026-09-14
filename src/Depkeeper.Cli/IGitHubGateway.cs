namespace Depkeeper.Cli;

/// <summary>
/// Exposes controller-owned GitHub operations independently of the repair agent.
/// </summary>
internal interface IGitHubGateway
{
    /// <summary>
    /// Lists open Dependabot pull requests for a repository.
    /// </summary>
    /// <param name="repository">The repository name.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns>Current eligible pull requests.</returns>
    Task<IReadOnlyList<PullRequestSnapshot>> ListAsync(string repository, CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes a pull request before making a decision.
    /// </summary>
    /// <param name="pullRequest">The pull request to refresh.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The latest revision and check state.</returns>
    Task<PullRequestSnapshot> RefreshAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves failed-job logs for diagnosis.
    /// </summary>
    /// <param name="pullRequest">The failing pull request.</param>
    /// <param name="cancellationToken">Cancels log retrieval.</param>
    /// <returns>Sanitized bounded logs.</returns>
    Task<string> GetFailureLogsAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves all unresolved inline review threads for the current PR revision.
    /// </summary>
    /// <param name="pullRequest">The PR to inspect.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The unresolved review conversations.</returns>
    Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves review threads after the requested paths pass independent validation.
    /// </summary>
    /// <param name="pullRequest">The exact verified PR revision.</param>
    /// <param name="threadIds">The unchanged reviewed threads to resolve.</param>
    /// <param name="cancellationToken">Cancels the mutations.</param>
    Task ResolveReviewThreadsAsync(PullRequestSnapshot pullRequest, IReadOnlyList<string> threadIds,
        CancellationToken cancellationToken);

    /// <summary>
    /// Updates an outdated branch without bypassing the expected-head check.
    /// </summary>
    /// <param name="pullRequest">The expected pull request revision.</param>
    /// <param name="cancellationToken">Cancels the update.</param>
    /// <returns>Whether GitHub accepted the branch update.</returns>
    Task<bool> UpdateBranchAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken);

    /// <summary>
    /// Merges exactly the inspected revision while respecting GitHub branch rules.
    /// </summary>
    /// <param name="pullRequest">The expected pull request revision.</param>
    /// <param name="cancellationToken">Cancels the merge request.</param>
    /// <returns>The confirmed merge commit, or null when GitHub has not confirmed a merge.</returns>
    Task<string?> MergeAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken);

    /// <summary>
    /// Retrieves checks, statuses, and workflow completion for an exact commit.
    /// </summary>
    /// <param name="repository">The repository name.</param>
    /// <param name="commit">The exact commit to verify.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The reported verification results.</returns>
    Task<IReadOnlyList<CheckSnapshot>> GetCommitChecksAsync(string repository, string commit, CancellationToken cancellationToken);

    /// <summary>
    /// Finds the current base revision only when it is a descendant of the tracked merged commit.
    /// </summary>
    /// <param name="repository">The repository name.</param>
    /// <param name="branch">The merged PR's base branch.</param>
    /// <param name="ancestor">The tracked merge commit.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>A later descendant commit, or null.</returns>
    Task<string?> GetBranchDescendantAsync(string repository, string branch, string ancestor, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a branch to its current exact commit.
    /// </summary>
    /// <param name="repository">The selected repository.</param>
    /// <param name="branch">The branch name.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The current branch commit.</returns>
    Task<string> GetBranchHeadAsync(string repository, string branch, CancellationToken cancellationToken);

    /// <summary>
    /// Requests a bounded refresh of the GitHub workflow runs represented by the supplied checks.
    /// </summary>
    /// <param name="repository">The selected repository.</param>
    /// <param name="commit">The exact revision those workflow runs must verify.</param>
    /// <param name="checks">The check evidence identifying the exact workflow runs.</param>
    /// <param name="failedOnly">Whether only failed jobs should be rerun.</param>
    /// <param name="cancellationToken">Cancels refresh requests.</param>
    /// <returns>Whether at least one workflow was successfully requested.</returns>
    Task<bool> RerunChecksAsync(string repository, string commit, IReadOnlyList<CheckSnapshot> checks, bool failedOnly,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds an existing recovery PR while verifying its branch, base, and authenticated author.
    /// </summary>
    /// <param name="request">The expected recovery identity.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The verified recovery PR, or null.</returns>
    Task<PullRequestSnapshot?> FindRecoveryAsync(RecoveryRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Creates or resumes a recovery PR for an independently validated branch.
    /// </summary>
    /// <param name="request">The recovery branch and source PR.</param>
    /// <param name="profile">The trusted PR assignment policy.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The created or existing recovery PR.</returns>
    Task<PullRequestSnapshot> CreateRecoveryPullRequestAsync(RecoveryRequest request, RepositoryProfile profile,
        CancellationToken cancellationToken);

    /// <summary>
    /// Publishes an actionable outcome on a pull request.
    /// </summary>
    /// <param name="pullRequest">The target pull request.</param>
    /// <param name="body">The sanitized comment.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    Task CommentAsync(PullRequestSnapshot pullRequest, string body, CancellationToken cancellationToken);
}
