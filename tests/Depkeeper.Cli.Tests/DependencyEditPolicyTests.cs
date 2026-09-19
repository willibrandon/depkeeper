using System.Text.Json;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Keeps registry and manifest edits subject to publication checks while permitting verified code-only repairs.
/// </summary>
[TestClass]
public sealed class DependencyEditPolicyTests
{
    /// <summary>
    /// Distinguishes ordinary source changes from dependency declarations and incomplete metadata.
    /// </summary>
    /// <param name="path">The changed repository path.</param>
    /// <param name="patch">The supplied patch text.</param>
    /// <param name="codeOnly">Whether publication metadata is unnecessary.</param>
    [TestMethod]
    [DataRow("src/code.cs", "+return a + b;", true)]
    [DataRow("LICENSES/example-1.0.0.txt", "-https://example.com/license", true)]
    [DataRow("package.json", "+new version", false)]
    [DataRow(".devcontainer/Dockerfile", "+FROM new-image", false)]
    [DataRow("scripts/setup.cs", "+#:package Example@1.0.0", false)]
    [DataRow("src/code.ts", "+import from https://example.com/new.js", false)]
    [DataRow("src/code.cs", null, false)]
    [DataRow("lib/postern/server.ex", "+def add(a, b), do: a + b", true)]
    [DataRow("mix.exs", "+      {:jason, \"~> 1.5\"},", false)]
    [DataRow("apps/web/mix.exs", "+      {:jason, \"~> 1.5\"},", false)]
    [DataRow("mix.lock", "+  \"jason\": {:hex, :jason, \"1.5.0\"},", false)]
    public void RequiresMetadataForPotentialDependencyEdits(string path, string? patch, bool codeOnly)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { filename = path, patch }));
        Assert.AreEqual(codeOnly, DependencyEditPolicy.IsCodeOnly(document.RootElement));
    }

    /// <summary>
    /// Treats malformed untrusted GitHub data as dependency-sensitive rather than throwing.
    /// </summary>
    [TestMethod]
    public void MalformedFileMetadataIsNotCodeOnly()
    {
        using var document = JsonDocument.Parse("{}");
        Assert.IsFalse(DependencyEditPolicy.IsCodeOnly(document.RootElement));
    }
}
