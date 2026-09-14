namespace Depkeeper.Cli;

/// <summary>
/// Captures one unresolved inline review conversation without treating its text as trusted instructions.
/// </summary>
/// <param name="Id">The GraphQL review thread identifier.</param>
/// <param name="Path">The reviewed repository path.</param>
/// <param name="Line">The current reviewed line, when available.</param>
/// <param name="LatestCommentId">The latest comment used to detect feedback added after a push.</param>
/// <param name="Conversation">The bounded review conversation.</param>
/// <param name="Url">The latest comment URL.</param>
internal sealed record ReviewThread(string Id, string Path, int? Line, string LatestCommentId, string Conversation, string Url);
