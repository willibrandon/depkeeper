namespace Depkeeper.Cli;

/// <summary>
/// Evaluates dependency age independently of PR creation time and agent claims.
/// </summary>
internal interface IReleaseAgeGate
{
    /// <summary>
    /// Evaluates publication metadata for all newly introduced dependency versions.
    /// </summary>
    /// <param name="pullRequest">The exact pull request revision.</param>
    /// <param name="policy">The deployment's release-age policy.</param>
    /// <param name="cancellationToken">Cancels metadata retrieval.</param>
    /// <returns>A hold reason, or null when the policy is satisfied.</returns>
    Task<string?> GetBlockerAsync(PullRequestSnapshot pullRequest, ReleaseAgePolicy policy, CancellationToken cancellationToken);
}
