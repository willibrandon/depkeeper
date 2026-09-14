namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies language-independent detection and explicit validation overrides.
/// </summary>
[TestClass]
public sealed class ToolchainDetectorTests
{
    /// <summary>
    /// Honors exact Node and npm declarations to reproduce the repository's validation environment.
    /// </summary>
    [TestMethod]
    public void HonorsDeclaredNodeAndNpmVersions()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-toolchain-").FullName;
        try
        {
            File.WriteAllText(Path.Join(directory, "package.json"), """
                {"engines":{"node":"24.19.0"},"packageManager":"npm@12.0.2","scripts":{"test":"node --test"}}
                """);
            var detected = ToolchainDetector.Resolve(directory, new RepositoryProfile());
            Assert.AreEqual("node:24.19.0-trixie", detected.Image);
            Assert.Contains("npm@12.0.2", detected.Install.First());
            Assert.AreEqual("npm install", detected.Install.Last());
            Assert.Contains("npm run test", detected.Verify);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Detects standard project manifests without requiring installed toolchains.
    /// </summary>
    /// <param name="manifest">The root manifest name.</param>
    /// <param name="expected">The expected toolchain family.</param>
    [TestMethod]
    [DataRow("Example.csproj", "dotnet")]
    [DataRow("Example.fsproj", "dotnet")]
    [DataRow("Example.vbproj", "dotnet")]
    [DataRow("Cargo.toml", "rust")]
    [DataRow("go.mod", "go")]
    [DataRow("pyproject.toml", "python")]
    [DataRow("pom.xml", "maven")]
    [DataRow("gradlew", "gradle")]
    [DataRow("Package.swift", "swift")]
    [DataRow("composer.json", "php")]
    [DataRow("Gemfile", "ruby")]
    public void DetectsProjectFamilies(string manifest, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-toolchain-").FullName;
        try
        {
            File.WriteAllText(Path.Join(directory, manifest), string.Empty);
            Assert.AreEqual(expected, ToolchainDetector.Resolve(directory, new RepositoryProfile()).Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Uses available npm checks without requiring a project-specific verify script.
    /// </summary>
    [TestMethod]
    public void DetectsNodeChecksAndHonorsOverrides()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-toolchain-").FullName;
        try
        {
            File.WriteAllText(Path.Join(directory, "package.json"), """{"scripts":{"check":"node --test"}}""");
            var detected = ToolchainDetector.Resolve(directory, new RepositoryProfile());
            Assert.AreEqual("node", detected.Name);
            Assert.Contains("npm run check", detected.Verify);
            var custom = ToolchainDetector.Resolve(directory,
                new RepositoryProfile("custom/image:latest", ["prepare"], ["verify"]));
            Assert.AreEqual("custom/image:latest", custom.Image);
            Assert.AreEqual("verify", custom.Verify.Single());
        }
        finally { Directory.Delete(directory, true); }
    }
}
