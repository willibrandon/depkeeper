namespace Depkeeper.Cli;

/// <summary>
/// Captures the identity, revision, and merge gates of a pull request.
/// </summary>
/// <param name="Repository">The owning repository.</param>
/// <param name="Number">The pull request number.</param>
/// <param name="Title">The pull request title.</param>
/// <param name="Author">The author login.</param>
/// <param name="Branch">The source branch.</param>
/// <param name="Head">The exact source commit.</param>
/// <param name="BaseBranch">The target branch.</param>
/// <param name="Draft">Whether the pull request is a draft.</param>
/// <param name="CrossRepository">Whether the source belongs to another repository.</param>
/// <param name="Mergeable">GitHub's conflict assessment.</param>
/// <param name="MergeState">GitHub's combined merge gate.</param>
/// <param name="ReviewDecision">The current review decision.</param>
/// <param name="Checks">The current reported checks.</param>
/// <param name="State">Whether the pull request is still open.</param>
internal sealed record PullRequestSnapshot(string Repository, int Number, string Title, string Author,
    string Branch, string Head, string BaseBranch, bool Draft, bool CrossRepository,
    string Mergeable, string MergeState, string ReviewDecision, IReadOnlyList<CheckSnapshot> Checks, string State = "OPEN")
{
    /// <summary>
    /// Gets the stable state key for this pull request.
    /// </summary>
    internal string Key => $"{Repository}#{Number}";

    /// <summary>
    /// Gets the public pull request URL.
    /// </summary>
    internal string Url => $"https://github.com/{Repository}/pull/{Number}";
}
