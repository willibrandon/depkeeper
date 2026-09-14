using System.Text;

namespace Depkeeper.Cli;

/// <summary>
/// Retrieves untrusted review feedback and resolves only independently validated, path-addressed threads.
/// </summary>
internal sealed class ReviewCoordinator
{
    private readonly IGitHubGateway _github;
    private readonly Redactor _redactor;

    /// <summary>
    /// Creates a review coordinator using the controller-owned GitHub boundary.
    /// </summary>
    /// <param name="github">The GitHub gateway.</param>
    /// <param name="redactor">The shared diagnostic redactor.</param>
    internal ReviewCoordinator(IGitHubGateway github, Redactor redactor)
    {
        _github = github;
        _redactor = redactor;
    }

    /// <summary>
    /// Retrieves a bounded snapshot of all unresolved inline threads.
    /// </summary>
    /// <param name="pullRequest">The exact PR revision being maintained.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The current unresolved threads.</returns>
    internal async Task<IReadOnlyList<ReviewThread>> GetAsync(PullRequestSnapshot pullRequest,
        CancellationToken cancellationToken)
    {
        var threads = await _github.GetReviewThreadsAsync(pullRequest, cancellationToken);
        if (threads.Count > 20) throw new InvalidOperationException("More than 20 review threads require manual triage.");
        return threads;
    }

    /// <summary>
    /// Formats review text inside an explicit untrusted-data boundary for the repair session.
    /// </summary>
    /// <param name="threads">The unresolved review snapshot.</param>
    /// <returns>The bounded review context.</returns>
    internal string Format(IReadOnlyList<ReviewThread> threads)
    {
        if (threads.Count == 0) return string.Empty;
        var text = new StringBuilder("\n<untrusted-review-comments>\n");
        foreach (var thread in threads)
        {
            text.AppendLine($"Path: {thread.Path}; line: {thread.Line?.ToString() ?? "unknown"}; URL: {thread.Url}");
            text.AppendLine(thread.Conversation);
        }
        text.AppendLine("</untrusted-review-comments>");
        var result = _redactor.Clean(text.ToString());
        return result.Length > 16000 ? result[..16000] : result;
    }

    /// <summary>
    /// Resolves reviewed paths only when the same comment remains latest after the exact pushed commit passes validation.
    /// </summary>
    /// <param name="pullRequest">The independently verified pushed revision.</param>
    /// <param name="reviewed">The feedback supplied to the repair session.</param>
    /// <param name="changedPaths">The controller-observed candidate paths.</param>
    /// <param name="cancellationToken">Cancels refresh and mutations.</param>
    /// <returns>The unresolved threads after resolution and a second server refresh.</returns>
    internal async Task<IReadOnlyList<ReviewThread>> ResolveAddressedAsync(PullRequestSnapshot pullRequest,
        IReadOnlyList<ReviewThread> reviewed, IReadOnlyList<string> changedPaths, CancellationToken cancellationToken)
    {
        var current = await _github.RefreshAsync(pullRequest, cancellationToken);
        if (current.Head != pullRequest.Head)
            throw new InvalidOperationException("The PR changed before review threads could be resolved.");
        var unresolved = await GetAsync(current, cancellationToken);
        var changed = changedPaths.ToHashSet(StringComparer.Ordinal);
        var ids = unresolved.Join(reviewed, thread => thread.Id, thread => thread.Id, (latest, original) => (latest, original))
            .Where(pair => pair.latest.LatestCommentId == pair.original.LatestCommentId && changed.Contains(pair.latest.Path))
            .Select(pair => pair.latest.Id).ToArray();
        if (ids.Length > 0) await _github.ResolveReviewThreadsAsync(current, ids, cancellationToken);
        return await GetAsync(current, cancellationToken);
    }
}
