using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Extracts exact GitHub Actions references from trusted base and PR-head workflow contents.
/// </summary>
internal static partial class WorkflowDependencyParser
{
    /// <summary>
    /// Compares remote action and reusable workflow references without interpreting workflow logic.
    /// </summary>
    /// <param name="before">The workflow at the exact base commit.</param>
    /// <param name="after">The workflow at the exact PR head.</param>
    /// <returns>Added and removed action dependency records.</returns>
    internal static IReadOnlyList<DependencyChange> Compare(string? before, string? after)
    {
        var oldReferences = Parse(before).ToHashSet();
        var newReferences = Parse(after).ToHashSet();
        return newReferences.Except(oldReferences).Select(reference => Change("added", reference))
            .Concat(oldReferences.Except(newReferences).Select(reference => Change("removed", reference))).ToArray();
    }

    /// <summary>
    /// Identifies the workflow and action metadata files whose uses entries Dependabot updates.
    /// </summary>
    /// <param name="path">The repository-relative path.</param>
    /// <returns>Whether the file is a top-level workflow or an action metadata file.</returns>
    internal static bool IsWorkflow(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)) return false;
        return path.StartsWith(".github/workflows/", StringComparison.Ordinal) && path.Count(c => c == '/') == 2 ||
            Path.GetFileNameWithoutExtension(path).Equals("action", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Name, string Version)> Parse(string? content) => content is null ? [] :
        UsesEntry().Matches(content).Select(match => (match.Groups["name"].Value, match.Groups["ref"].Value));

    private static DependencyChange Change(string type, (string Name, string Version) reference) =>
        new(type, "actions", reference.Name, reference.Version, []);

    [GeneratedRegex(@"(?m)^[ \t]*(?:-[ \t]+)?uses:[ \t]*['""]?(?<name>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+(?:/[^@\s'""#]+)?)" +
        @"@(?<ref>[^\s'""#]+)", RegexOptions.CultureInvariant)]
    private static partial Regex UsesEntry();
}
