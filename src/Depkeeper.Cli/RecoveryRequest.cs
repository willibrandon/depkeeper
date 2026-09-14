namespace Depkeeper.Cli;

/// <summary>
/// Identifies a controller-owned repair branch rooted at the current failing base revision.
/// </summary>
/// <param name="Repository">The selected repository.</param>
/// <param name="SourcePullRequest">The dependency PR whose merge needs recovery.</param>
/// <param name="BaseBranch">The affected base branch.</param>
/// <param name="BaseHead">The exact base commit to repair.</param>
/// <param name="Branch">The deterministic recovery branch.</param>
internal sealed record RecoveryRequest(string Repository, int SourcePullRequest, string BaseBranch, string BaseHead, string Branch);
