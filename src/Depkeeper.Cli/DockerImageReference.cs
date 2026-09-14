using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Represents a fully pinned Docker image reference suitable for registry metadata verification.
/// </summary>
/// <param name="Registry">The normalized registry host.</param>
/// <param name="Namespace">The Docker Hub namespace.</param>
/// <param name="Repository">The image repository.</param>
/// <param name="Tag">The mutable tag whose registry timestamp is inspected.</param>
/// <param name="Digest">The exact sha256 manifest digest.</param>
internal sealed partial record DockerImageReference(string Registry, string Namespace, string Repository, string Tag, string Digest)
{
    /// <summary>
    /// Gets the canonical dependency name used by the publication client.
    /// </summary>
    internal string Name => $"{Registry}/{Namespace}/{Repository}";

    /// <summary>
    /// Gets the concrete version composed from the tag and verified digest.
    /// </summary>
    internal string Version => $"{Tag}@sha256:{Digest}";

    /// <summary>
    /// Parses a Dockerfile image reference only when it contains a full tag and sha256 digest.
    /// </summary>
    /// <param name="value">The image reference.</param>
    /// <param name="reference">The normalized pinned reference.</param>
    /// <returns>Whether parsing succeeded.</returns>
    internal static bool TryParse(string value, out DockerImageReference? reference)
    {
        reference = null;
        var match = PinnedImage().Match(value);
        if (!match.Success) return false;
        var path = match.Groups["name"].Value.Split('/');
        var hasRegistry = path.Length > 1 && (path[0].Contains('.', StringComparison.Ordinal) ||
            path[0].Contains(':', StringComparison.Ordinal) || path[0] == "localhost");
        var registry = hasRegistry ? path[0] : "docker.io";
        if (registry is "index.docker.io" or "registry-1.docker.io") registry = "docker.io";
        var components = hasRegistry ? path[1..] : path;
        if (components.Length == 1) components = ["library", components[0]];
        if (components.Length != 2) return false;
        reference = new DockerImageReference(registry, components[0], components[1], match.Groups["tag"].Value,
            match.Groups["digest"].Value);
        return true;
    }

    /// <summary>
    /// Rehydrates a dependency change for registry lookup.
    /// </summary>
    /// <param name="dependency">The dependency-review record.</param>
    /// <param name="reference">The normalized pinned image.</param>
    /// <returns>Whether the record contains a complete Docker reference.</returns>
    internal static bool TryParse(DependencyChange dependency, out DockerImageReference? reference) =>
        TryParse(dependency.Name + ":" + dependency.Version, out reference);

    [GeneratedRegex(@"\A(?<name>[a-z0-9][a-z0-9._:/-]*):(?<tag>[A-Za-z0-9_][A-Za-z0-9_.-]{0,127})" +
        @"@sha256:(?<digest>[a-f0-9]{64})\z", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedImage();
}
