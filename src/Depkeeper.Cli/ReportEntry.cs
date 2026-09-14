namespace Depkeeper.Cli;

/// <summary>
/// Describes a verified maintenance outcome without exposing raw agent transcripts.
/// </summary>
/// <param name="Repository">The repository inspected.</param>
/// <param name="Number">The pull request number, or zero for repository failures.</param>
/// <param name="Outcome">The maintenance outcome.</param>
/// <param name="Detail">The sanitized result and next action.</param>
internal sealed record ReportEntry(string Repository, int Number, string Outcome, string Detail);
