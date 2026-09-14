namespace Depkeeper.Cli;

/// <summary>
/// Remembers the single recovery branch and bounded work associated with a failed merge.
/// </summary>
/// <param name="Branch">The controller-owned recovery branch.</param>
/// <param name="BaseHead">The base revision used to create the recovery.</param>
/// <param name="Attempts">The repair sessions already consumed.</param>
/// <param name="PullRequest">The recovery PR number, once created.</param>
/// <param name="Head">The latest independently validated repair commit.</param>
/// <param name="Blocked">Whether the recovery needs an explicit retry.</param>
/// <param name="Reason">The most recent repair or validation failure.</param>
/// <param name="CiRetried">Whether a failed CI run was already retried before proposing code changes.</param>
internal sealed record RecoveryState(string Branch, string BaseHead, int Attempts = 0, int? PullRequest = null,
    string? Head = null, bool Blocked = false, string? Reason = null, bool CiRetried = false);
