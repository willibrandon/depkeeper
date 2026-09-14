using System.Text.Json;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies range resolution against the correct locked workspace and installation location.
/// </summary>
[TestClass]
public sealed class NpmLockResolverTests
{
    /// <summary>
    /// Resolves hoisted packages and prefers workspace-local installed versions.
    /// </summary>
    [TestMethod]
    public void ResolvesTheNearestLockedDependency()
    {
        using var document = JsonDocument.Parse("""
            {"packages":{
              "packages/server":{"dependencies":{"vscode-uri":"^3.2.0","other":"^1.0.0"}},
              "node_modules/vscode-uri":{"version":"3.2.0"},
              "node_modules/other":{"version":"1.0.0"},
              "packages/server/node_modules/other":{"version":"1.1.0"}
            }}
            """);
        Assert.AreEqual("3.2.0", NpmLockResolver.Resolve(document.RootElement, "packages/server/package.json", "vscode-uri", "^3.2.0"));
        Assert.AreEqual("1.1.0", NpmLockResolver.Resolve(document.RootElement, "packages/server/package.json", "other", "^1.0.0"));
        Assert.IsNull(NpmLockResolver.Resolve(document.RootElement, "packages/other/package.json", "vscode-uri", "^3.2.0"));
        Assert.IsNull(NpmLockResolver.Resolve(document.RootElement, "packages/server/package.json", "vscode-uri", "^4.0.0"));
    }
}
