namespace Depkeeper.Cli;

/// <summary>
/// Persists repair progress so unchanged blocked revisions are not retried daily.
/// </summary>
/// <param name="Head">The revision to which the state applies.</param>
/// <param name="Attempts">The repair attempts already consumed.</param>
/// <param name="Blocked">Whether automatic repairs must stop for this revision.</param>
/// <param name="Reason">A sanitized explanation.</param>
/// <param name="UpdatedAt">The last state transition.</param>
/// <param name="MergeHead">A merged commit whose post-merge verification is still tracked.</param>
/// <param name="MergeBranch">The base branch to inspect for a verified follow-up fix.</param>
/// <param name="Recovery">The bounded recovery PR associated with a failed merge.</param>
/// <param name="BaseHead">The base revision observed when this state was recorded.</param>
/// <param name="RebaseRequested">Whether the controller asked Dependabot to rebase this revision onto <paramref name="BaseHead"/>.</param>
internal sealed record AttemptState(string Head, int Attempts, bool Blocked, string Reason, DateTimeOffset UpdatedAt,
    string? MergeHead = null, string? MergeBranch = null, RecoveryState? Recovery = null, string? BaseHead = null,
    bool RebaseRequested = false);
