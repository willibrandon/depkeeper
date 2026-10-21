namespace Depkeeper.Cli;

/// <summary>
/// Signals that GitHub dependency review is unavailable and no independent parser covers the changed files.
/// </summary>
internal sealed class DependencyReviewUnavailableException : IOException
{
    /// <summary>
    /// Creates the exception with an operator-facing explanation.
    /// </summary>
    internal DependencyReviewUnavailableException()
        : base("GitHub dependency review is unavailable for this repository, so publication age cannot be verified. " +
            "Enable the repository's dependency graph.")
    {
    }
}
