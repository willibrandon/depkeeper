using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Resolves manifest ranges through the matching npm workspace lock entry rather than guessing a package version.
/// </summary>
internal static partial class NpmLockResolver
{
    /// <summary>
    /// Identifies a concrete npm version suitable for publication lookup.
    /// </summary>
    /// <param name="version">The reported version or range.</param>
    /// <returns>Whether the value is an exact semantic version.</returns>
    internal static bool IsExact(string version) => ExactVersion().IsMatch(version);

    /// <summary>
    /// Finds the locked version in the workspace's nearest installed dependency location.
    /// </summary>
    /// <param name="document">The npm lockfile at the exact PR head.</param>
    /// <param name="manifest">The declaring manifest relative to the lockfile directory.</param>
    /// <param name="name">The dependency name.</param>
    /// <param name="range">The declaration reported by GitHub.</param>
    /// <returns>The locked version or null when the lockfile does not establish the resolution.</returns>
    internal static string? Resolve(JsonElement document, string manifest, string name, string range)
    {
        if (!document.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object) return null;
        var directory = manifest.Contains('/') ? manifest[..manifest.LastIndexOf('/')] : string.Empty;
        if (!packages.TryGetProperty(directory, out var workspace)) return null;
        string[] fields = ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies"];
        var declared = fields.Any(field => workspace.TryGetProperty(field, out var dependencies) &&
            dependencies.ValueKind == JsonValueKind.Object && dependencies.TryGetProperty(name, out var value) &&
            value.ValueKind == JsonValueKind.String && value.GetString() == range);
        if (!declared) return null;
        while (true)
        {
            var location = (directory.Length == 0 ? string.Empty : directory + "/") + "node_modules/" + name;
            if (packages.TryGetProperty(location, out var installed))
            {
                if (installed.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.True) return null;
                return installed.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String &&
                    IsExact(version.GetString()!) ? version.GetString() : null;
            }
            if (directory.Length == 0) return null;
            directory = directory.Contains('/') ? directory[..directory.LastIndexOf('/')] : string.Empty;
        }
    }

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersion();
}
