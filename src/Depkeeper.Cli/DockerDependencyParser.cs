using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Extracts exact Docker image changes from trusted base and PR-head Dockerfile contents.
/// </summary>
internal static partial class DockerDependencyParser
{
    /// <summary>
    /// Compares all fully pinned FROM references without interpreting build instructions.
    /// </summary>
    /// <param name="before">The Dockerfile at the exact base commit.</param>
    /// <param name="after">The Dockerfile at the exact PR head.</param>
    /// <returns>Added and removed Docker dependency records.</returns>
    internal static IReadOnlyList<DependencyChange> Compare(string? before, string? after)
    {
        var oldReferences = Parse(before).ToHashSet();
        var newReferences = Parse(after).ToHashSet();
        return newReferences.Except(oldReferences).Select(reference => Change("added", reference))
            .Concat(oldReferences.Except(newReferences).Select(reference => Change("removed", reference))).ToArray();
    }

    /// <summary>
    /// Identifies file names whose FROM instructions define Docker dependencies.
    /// </summary>
    /// <param name="path">The repository-relative path.</param>
    /// <returns>Whether the file is a Dockerfile or Containerfile variant.</returns>
    internal static bool IsDockerfile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Dockerfile.", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Containerfile.", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<DockerImageReference> Parse(string? content) => content is null ? [] :
        FromInstruction().Matches(content).Cast<Match>()
            .Select(match => DockerImageReference.TryParse(match.Groups["image"].Value, out var reference) ? reference : null)
            .OfType<DockerImageReference>();

    private static DependencyChange Change(string type, DockerImageReference reference) =>
        new(type, "docker", reference.Name, reference.Version, []);

    [GeneratedRegex(@"(?im)^\s*FROM(?:\s+--platform=\S+)?\s+(?<image>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex FromInstruction();
}
