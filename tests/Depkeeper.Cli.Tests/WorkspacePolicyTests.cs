namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies filesystem containment and protection of trusted validation infrastructure.
/// </summary>
[TestClass]
public sealed class WorkspacePolicyTests
{
    /// <summary>
    /// Restricts generated-license cleanup to inventory text entries rather than source files or tests.
    /// </summary>
    [TestMethod]
    public void RecognizesOnlyGeneratedLicenseEntries()
    {
        Assert.IsTrue(WorkspacePolicy.IsGeneratedLicense("LICENSES/ignore-7.0.6.txt"));
        Assert.IsFalse(WorkspacePolicy.IsGeneratedLicense("test/example.test.ts"));
        Assert.IsFalse(WorkspacePolicy.IsGeneratedLicense("LICENSES/../test.txt"));
        Assert.IsFalse(WorkspacePolicy.IsGeneratedLicense("LICENSES/source.cs"));
        Assert.IsFalse(WorkspacePolicy.IsGeneratedLicense("LICENSE"));
    }

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

    /// <summary>
    /// Identifies the exact protected fields changed by an unsafe repair.
    /// </summary>
    [TestMethod]
    public void ReportsChangedProtectedManifestFields()
    {
        const string before = """
            {"name":"example","packageManager":"npm@12.0.2","scripts":{"test":"node --test"},"dependencies":{"a":"1"}}
            """;
        const string after = """
            {"name":"example","packageManager":"npm@12.0.3","scripts":{"test":"true"},"dependencies":{"a":"2"},"private":true}
            """;

        var fields = WorkspacePolicy.ChangedProtectedManifestFields(before, after);

        Assert.HasCount(3, fields);
        Assert.Contains("packageManager", fields);
        Assert.Contains("private", fields);
        Assert.Contains("scripts", fields);
    }

    /// <summary>
    /// Rejects a repair that undoes any direct dependency value introduced by the Dependabot head.
    /// </summary>
    [TestMethod]
    public void ReportsRevertedDependencyUpdates()
    {
        const string baseline = """
            {"dependencies":{"runtime":"1.0.0","removed":"1.0.0"},"devDependencies":{"tool":"2.0.0","unchanged":"3.0.0"}}
            """;
        const string expected = """
            {"dependencies":{"runtime":"1.1.0","added":"1.0.0"},"devDependencies":{"tool":"2.1.0","unchanged":"3.0.0"}}
            """;
        const string candidate = """
            {"dependencies":{"runtime":"1.0.0","removed":"1.0.0"},"devDependencies":{"tool":"2.2.0","unchanged":"4.0.0"}}
            """;

        var reverted = WorkspacePolicy.RevertedDependencyUpdates(baseline, expected, candidate);

        Assert.HasCount(4, reverted);
        Assert.Contains("dependencies.added", reverted);
        Assert.Contains("dependencies.removed", reverted);
        Assert.Contains("dependencies.runtime", reverted);
        Assert.Contains("devDependencies.tool", reverted);
        Assert.DoesNotContain("devDependencies.unchanged", reverted);
    }

    /// <summary>
    /// Allows repairs to add dependency fixes while retaining the exact values selected by Dependabot.
    /// </summary>
    [TestMethod]
    public void AllowsAdditionalDependencyRepairWithoutRevertingTheUpdate()
    {
        const string baseline = """{"devDependencies":{"tool":"2.0.0"}}""";
        const string expected = """{"devDependencies":{"tool":"2.1.0"}}""";
        const string candidate = """{"devDependencies":{"tool":"2.1.0","audit-fix":"1.0.0"}}""";

        Assert.IsEmpty(WorkspacePolicy.RevertedDependencyUpdates(baseline, expected, candidate));
    }

    /// <summary>
    /// Allows an existing install permission to follow its exact dependency update without broadening script execution.
    /// </summary>
    [TestMethod]
    public void AllowsExactDependencyPermissionMigration()
    {
        const string before = """
            {"devDependencies":{"tree-sitter-cli":"0.26.12"},"allowScripts":{"tree-sitter-cli@0.26.12":true}}
            """;
        const string update = """
            {"devDependencies":{"tree-sitter-cli":"0.26.13"},"allowScripts":{"tree-sitter-cli@0.26.13":true}}
            """;
        const string broadened = """
            {"devDependencies":{"tree-sitter-cli":"0.26.13"},"allowScripts":{"tree-sitter-cli@0.26.13":true,"other@1.0.0":true}}
            """;
        const string changedPermission = """
            {"devDependencies":{"tree-sitter-cli":"0.26.13"},"allowScripts":{"tree-sitter-cli@0.26.13":false}}
            """;
        Assert.IsTrue(WorkspacePolicy.PreservesManifest(before, update));
        Assert.IsFalse(WorkspacePolicy.PreservesManifest(before, broadened));
        Assert.IsFalse(WorkspacePolicy.PreservesManifest(before, changedPermission));
    }

    /// <summary>
    /// Allows a stale permission key to catch up to an exact dependency that Dependabot already updated.
    /// </summary>
    [TestMethod]
    public void AllowsStaleExactPermissionCorrection()
    {
        const string before = """
            {"devDependencies":{"tree-sitter-cli":"0.27.0"},"allowScripts":{"tree-sitter-cli@0.26.13":true}}
            """;
        const string corrected = """
            {"devDependencies":{"tree-sitter-cli":"0.27.0"},"allowScripts":{"tree-sitter-cli@0.27.0":true}}
            """;
        const string changedAgain = """
            {"devDependencies":{"tree-sitter-cli":"0.28.0"},"allowScripts":{"tree-sitter-cli@0.28.0":true}}
            """;
        Assert.IsTrue(WorkspacePolicy.PreservesManifest(before, corrected));
        Assert.IsFalse(WorkspacePolicy.PreservesManifest(before, changedAgain));
    }
}
