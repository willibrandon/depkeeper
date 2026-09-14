namespace Depkeeper.Cli;

/// <summary>
/// Performs bounded repairs without deciding whether a pull request may merge.
/// </summary>
internal interface IRepairer
{
    /// <summary>
    /// Repairs, independently validates, scans, and pushes a candidate revision.
    /// </summary>
    /// <param name="pullRequest">The expected original revision.</param>
    /// <param name="logs">Sanitized failure logs.</param>
    /// <param name="profile">Trusted validation settings.</param>
    /// <param name="cancellationToken">Cancels the entire repair.</param>
    /// <returns>The verified candidate revision.</returns>
    Task<RepairResult> RepairAsync(PullRequestSnapshot pullRequest, string logs, RepositoryProfile profile,
        CancellationToken cancellationToken);
}
