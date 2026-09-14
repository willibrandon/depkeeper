namespace Depkeeper.Cli;

/// <summary>
/// Persists repair progress so unchanged blocked revisions are not retried daily.
/// </summary>
/// <param name="Head">The revision to which the state applies.</param>
/// <param name="Attempts">The repair attempts already consumed.</param>
/// <param name="Blocked">Whether automatic repairs must stop for this revision.</param>
/// <param name="Reason">A sanitized explanation.</param>
/// <param name="UpdatedAt">The last state transition.</param>
internal sealed record AttemptState(string Head, int Attempts, bool Blocked, string Reason, DateTimeOffset UpdatedAt);
