using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Compares exact public npm package versions recorded in package-lock files.
/// </summary>
internal static class NpmLockParser
{
    /// <summary>
    /// Returns exact added and removed registry package versions between two lock files.
    /// </summary>
    /// <param name="before">The base revision's lock content, or null for a new lock.</param>
    /// <param name="after">The head revision's lock content, or null for a removed lock.</param>
    /// <returns>The unique version changes in deterministic order.</returns>
    internal static IReadOnlyList<DependencyChange> Compare(string? before, string? after)
    {
        var oldPackages = Read(before);
        var newPackages = Read(after);
        var removed = oldPackages.Except(newPackages).OrderBy(package => package.Name, StringComparer.Ordinal)
            .ThenBy(package => package.Version, StringComparer.Ordinal)
            .Select(package => new DependencyChange("removed", package.Ecosystem, package.Name, package.Version, []));
        var added = newPackages.Except(oldPackages).OrderBy(package => package.Name, StringComparer.Ordinal)
            .ThenBy(package => package.Version, StringComparer.Ordinal)
            .Select(package => new DependencyChange("added", package.Ecosystem, package.Name, package.Version, []));
        return removed.Concat(added).ToArray();
    }

    private static HashSet<(string Ecosystem, string Name, string Version)> Read(string? content)
    {
        if (content is null) return [];
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object)
            throw new JsonException("The npm lock does not contain a packages object.");
        var result = new HashSet<(string Ecosystem, string Name, string Version)>();
        foreach (var package in packages.EnumerateObject())
        {
            if (!TryGetName(package.Name, out var name) || package.Value.ValueKind != JsonValueKind.Object ||
                package.Value.TryGetProperty("link", out var link) && link.ValueKind == JsonValueKind.True ||
                !package.Value.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(version.GetString())) continue;
            var ecosystem = package.Value.TryGetProperty("resolved", out var resolved) &&
                resolved.ValueKind == JsonValueKind.String && !IsPublicRegistry(resolved.GetString()!) ? "npm-source" : "npm";
            result.Add((ecosystem, name!, version.GetString()!));
        }
        return result;
    }

    private static bool IsPublicRegistry(string resolved) =>
        resolved.StartsWith("https://registry.npmjs.org/", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetName(string path, out string? name)
    {
        const string marker = "node_modules/";
        var index = path.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            name = null;
            return false;
        }
        var installed = path[(index + marker.Length)..];
        var parts = installed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var count = parts.Length > 0 && parts[0].StartsWith('@') ? 2 : 1;
        if (parts.Length < count)
        {
            name = null;
            return false;
        }
        name = string.Join('/', parts.Take(count));
        return true;
    }
}
