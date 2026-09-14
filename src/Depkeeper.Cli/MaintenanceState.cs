namespace Depkeeper.Cli;

/// <summary>
/// Stores durable per-pull-request maintenance state.
/// </summary>
/// <param name="Version">The state format version.</param>
/// <param name="PullRequests">State indexed by repository and pull request number.</param>
/// <param name="NextRepository">The next repository index for fair use of the repair budget.</param>
internal sealed record MaintenanceState(int Version, Dictionary<string, AttemptState> PullRequests, int NextRepository = 0);
