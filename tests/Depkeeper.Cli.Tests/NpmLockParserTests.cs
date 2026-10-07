using System.Text.Json;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies exact npm version comparison from package-lock installation records.
/// </summary>
[TestClass]
public sealed class NpmLockParserTests
{
    /// <summary>
    /// Reports unique registry package version changes while ignoring workspaces and links.
    /// </summary>
    [TestMethod]
    public void ComparesUniqueInstalledRegistryVersions()
    {
        const string before = """
            {"packages":{
              "":{"name":"example","version":"1.0.0"},
              "node_modules/example":{"version":"1.0.0","resolved":"https://registry.npmjs.org/example/-/example-1.0.0.tgz"},
              "packages/app/node_modules/example":{"version":"1.0.0"},
              "packages/app":{"name":"app","version":"2.0.0"},
              "node_modules/linked":{"resolved":"packages/linked","link":true}
            }}
            """;
        const string after = """
            {"packages":{
              "":{"name":"example","version":"1.0.0"},
              "node_modules/example":{"version":"1.1.0","resolved":"https://registry.npmjs.org/example/-/example-1.1.0.tgz"},
              "packages/app/node_modules/example":{"version":"1.1.0"},
              "node_modules/@scope/tool":{"version":"3.0.0"},
              "node_modules/source":{"version":"4.0.0","resolved":"git+https://github.com/example/source.git#abcdef"},
              "packages/app":{"name":"app","version":"2.0.0"},
              "node_modules/linked":{"resolved":"packages/linked","link":true}
            }}
            """;

        var changes = NpmLockParser.Compare(before, after);

        Assert.HasCount(4, changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Name == "example" &&
            change.Version == "1.0.0", changes);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Ecosystem == "npm" && change.Name == "example" &&
            change.Version == "1.1.0", changes);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Name == "@scope/tool" &&
            change.Version == "3.0.0", changes);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Ecosystem == "npm-source" &&
            change.Name == "source", changes);
    }

    /// <summary>
    /// Treats a new or removed lock as additions or removals without inventing root workspace packages.
    /// </summary>
    [TestMethod]
    public void SupportsAddedAndRemovedLocks()
    {
        const string content = """{"packages":{"":{"name":"root","version":"1.0.0"},"node_modules/a":{"version":"2.0.0"}}}""";

        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Name == "a",
            NpmLockParser.Compare(null, content));
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Name == "a",
            NpmLockParser.Compare(content, null));
    }

    /// <summary>
    /// Rejects legacy or incomplete lock content that cannot establish exact installed packages.
    /// </summary>
    [TestMethod]
    public void RejectsLocksWithoutPackageRecords()
    {
        Assert.ThrowsExactly<JsonException>(() => NpmLockParser.Compare("{}", "{}"));
    }
}
