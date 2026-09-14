using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Conservatively identifies recovery edits that still need dependency publication metadata.
/// </summary>
internal static class DependencyEditPolicy
{
    /// <summary>
    /// Rejects manifests, incomplete patches, and changed package or external-source declarations.
    /// </summary>
    /// <param name="file">GitHub's changed-file record.</param>
    /// <returns>Whether the patch is an ordinary code-only change.</returns>
    internal static bool IsCodeOnly(JsonElement file)
    {
        if (!file.TryGetProperty("filename", out var filename) || filename.ValueKind != JsonValueKind.String) return false;
        var path = filename.GetString()!;
        var name = Path.GetFileName(path).ToLowerInvariant();
        string[] names = ["package.json", "composer.json", "gemfile", "pom.xml", "go.mod", "go.sum", "package.swift",
            "global.json", "project.json", "dotnet-tools.json", "makefile", ".npmrc", ".yarnrc"];
        string[] suffixes = [".lock", ".toml", ".csproj", ".fsproj", ".vbproj", ".props", ".targets", ".gemspec",
            ".nuspec", ".gradle", ".kts", ".sbt", ".zon", ".yaml", ".yml"];
        if (names.Contains(name, StringComparer.Ordinal) || suffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)) ||
            name.Contains("lock", StringComparison.Ordinal) || name.StartsWith("requirements", StringComparison.Ordinal) ||
            name.Contains("dockerfile", StringComparison.Ordinal) || name.Contains("containerfile", StringComparison.Ordinal) ||
            name == "build.zig") return false;
        if (!file.TryGetProperty("patch", out var patch) || patch.ValueKind != JsonValueKind.String) return false;
        if (WorkspacePolicy.IsGeneratedLicense(path)) return true;
        string[] declarations = ["#:package", "nuget:", "//> using dep", "dependencies", "http://", "https://", "npm:", "jsr:",
            "pip install", "npm install", "cargo add", "go get"];
        return patch.GetString()!.Split('\n').Where(line => line.StartsWith('+') || line.StartsWith('-'))
            .All(line => !declarations.Any(declaration => line.Contains(declaration, StringComparison.OrdinalIgnoreCase)));
    }
}
