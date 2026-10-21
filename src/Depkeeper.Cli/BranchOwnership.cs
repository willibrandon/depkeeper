namespace Depkeeper.Cli;

/// <summary>
/// Describes who contributed the commits on a pull request branch, which decides how it can be brought up to date.
/// </summary>
internal enum BranchOwnership
{
    /// <summary>
    /// Every commit was authored by Dependabot, so Dependabot will rebase the branch on request.
    /// </summary>
    Dependabot,

    /// <summary>
    /// Dependabot's commits are joined only by merges from the base, which Dependabot will overwrite when asked to recreate.
    /// </summary>
    DependabotWithMerges,

    /// <summary>
    /// Dependabot's commits are joined by controller repairs of this PR, which a merge from the base keeps and a recreation discards.
    /// </summary>
    DependabotWithRepairs,

    /// <summary>
    /// The branch carries a controller or human commit that a Dependabot rewrite would discard.
    /// </summary>
    Foreign
}
