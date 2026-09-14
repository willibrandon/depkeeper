namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies filesystem containment and protection of trusted validation infrastructure.
/// </summary>
[TestClass]
public sealed class WorkspacePolicyTests
{
    /// <summary>
    /// Rejects traversal, sibling directories, Git metadata, and verification policy edits.
    /// </summary>
    [TestMethod]
    public void RestrictsAccessToTheCheckout()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-policy-").FullName;
        try
        {
            var policy = new WorkspacePolicy(directory);
            Assert.IsTrue(policy.Allows("src/code.cs", true));
            Assert.IsTrue(policy.Allows(".github/workflows/ci.yml", false));
            Assert.IsFalse(policy.Allows("../outside.txt", false));
            Assert.IsFalse(policy.Allows(directory + "-sibling/file.cs", true));
            Assert.IsFalse(policy.Allows(".git/config", true));
            Assert.IsFalse(policy.Allows(".GIT/config", true));
            Assert.IsFalse(policy.Allows(".env.production", false));
            Assert.IsFalse(policy.Allows(".github/workflows/ci.yml", true));
            Assert.IsFalse(policy.Allows(".editorconfig", true));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Allows dependency updates while rejecting replacement of trusted npm scripts.
    /// </summary>
    [TestMethod]
    public void PreservesVerificationCommandsAndPackageIdentity()
    {
        const string before = """{"name":"example","scripts":{"test":"node --test"},"dependencies":{"a":"1"}}""";
        const string update = """{"name":"example","scripts":{"test":"node --test"},"dependencies":{"a":"2"}}""";
        const string bypass = """{"name":"example","scripts":{"test":"true"},"dependencies":{"a":"2"}}""";
        Assert.IsTrue(WorkspacePolicy.PreservesManifest(before, update));
        Assert.IsFalse(WorkspacePolicy.PreservesManifest(before, bypass));
    }
}
