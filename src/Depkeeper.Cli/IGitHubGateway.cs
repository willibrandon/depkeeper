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
    /// <returns>Whether GitHub accepted the merge.</returns>
    Task<bool> MergeAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken);

    /// <summary>
    /// Publishes an actionable outcome on a pull request.
    /// </summary>
    /// <param name="pullRequest">The target pull request.</param>
    /// <param name="body">The sanitized comment.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    Task CommentAsync(PullRequestSnapshot pullRequest, string body, CancellationToken cancellationToken);
}
