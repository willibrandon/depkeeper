using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Constrains agent file access and rejects changes that weaken trusted verification infrastructure.
/// </summary>
internal sealed class WorkspacePolicy
{
    private readonly string _root;

    /// <summary>
    /// Creates a policy scoped to one isolated checkout.
    /// </summary>
    /// <param name="directory">The checkout directory.</param>
    internal WorkspacePolicy(string directory) => _root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>
    /// Determines whether a requested path is contained and free of symbolic-link traversal.
    /// </summary>
    /// <param name="path">The requested absolute or relative path.</param>
    /// <param name="write">Whether the operation modifies a file.</param>
    /// <returns>Whether the operation is allowed.</returns>
    internal bool Allows(string path, bool write)
    {
        try
        {
            var full = Path.GetFullPath(path, _root);
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, comparison)) return false;
            var relative = Path.GetRelativePath(_root, full).Replace('\\', '/');
            if (relative.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                part.StartsWith(".env", StringComparison.OrdinalIgnoreCase) &&
                    !part.EndsWith(".example", StringComparison.OrdinalIgnoreCase) &&
                    !part.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
                part.EndsWith(".key", StringComparison.OrdinalIgnoreCase))) return false;
            if (write && IsProtected(relative)) return false;
            for (var current = full; current != _root; current = Path.GetDirectoryName(current)!)
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Identifies paths reserved for the controller or repository security policy.
    /// </summary>
    /// <param name="relative">A repository-relative path.</param>
    /// <returns>Whether automatic modification is prohibited.</returns>
    internal static bool IsProtected(string relative)
    {
        var parts = relative.Replace('\\', '/').ToLowerInvariant().Split('/');
        return parts.Any(part => part is ".git" or ".github" or ".claude" or ".copilot" or ".vscode" ||
            part.StartsWith(".env", StringComparison.OrdinalIgnoreCase)) ||
            parts[^1] is "agents.md" or "claude.md" or ".editorconfig" or ".gitattributes" or ".gitignore" or
                ".gitleaks.toml" or ".gitleaksignore" or ".picket.toml" or ".picketignore" or "depkeeper.json";
    }

    /// <summary>
    /// Verifies that dependency edits preserve existing npm scripts and package identity.
    /// </summary>
    /// <param name="before">The manifest before repair.</param>
    /// <param name="after">The manifest after repair.</param>
    /// <returns>Whether controller-selected commands and package identity remain intact.</returns>
    internal static bool PreservesManifest(string before, string after)
    {
        using var left = JsonDocument.Parse(before);
        using var right = JsonDocument.Parse(after);
        string[] dependencyFields =
            ["dependencies", "devDependencies", "optionalDependencies", "peerDependencies", "overrides", "resolutions"];
        foreach (var property in left.RootElement.EnumerateObject()
            .Where(property => !dependencyFields.Contains(property.Name, StringComparer.Ordinal)))
        {
            if (!right.RootElement.TryGetProperty(property.Name, out var value) || !JsonElement.DeepEquals(property.Value, value))
                return false;
        }
        return right.RootElement.EnumerateObject().All(property => dependencyFields.Contains(property.Name, StringComparer.Ordinal) ||
            left.RootElement.TryGetProperty(property.Name, out _));
    }
}
