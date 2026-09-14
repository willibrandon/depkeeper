namespace Depkeeper.Cli;

/// <summary>
/// Records a controller-verified repair commit and a sanitized agent summary.
/// </summary>
/// <param name="Head">The pushed revision, or null when no repair was produced.</param>
/// <param name="Summary">The diagnostic summary.</param>
/// <param name="ChangedPaths">The paths independently observed in the validated candidate.</param>
internal sealed record RepairResult(string? Head, string Summary, string[]? ChangedPaths = null);
