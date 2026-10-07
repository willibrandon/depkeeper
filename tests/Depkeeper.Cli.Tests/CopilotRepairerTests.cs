namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies deterministic repair selection before an agent session is used.
/// </summary>
[TestClass]
public sealed class CopilotRepairerTests
{
    /// <summary>
    /// Limits npm audit remediation to Node repositories with a package lock and audit evidence.
    /// </summary>
    /// <param name="toolchain">The detected toolchain family.</param>
    /// <param name="hasLock">Whether the checkout contains a package lock.</param>
    /// <param name="logs">The failed-check evidence.</param>
    /// <param name="expected">Whether deterministic remediation is eligible.</param>
    [TestMethod]
    [DataRow("node", true, "# npm audit report", true)]
    [DataRow("node", true, "npm audit --audit-level=moderate", false)]
    [DataRow("node", false, "# npm audit report", false)]
    [DataRow("node", true, "TypeScript compilation failed", false)]
    [DataRow("dotnet", true, "# npm audit report", false)]
    public void SelectsOnlyNpmAuditFailures(string toolchain, bool hasLock, string logs, bool expected)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-repair-").FullName;
        try
        {
            if (hasLock) File.WriteAllText(Path.Join(directory, "package-lock.json"), "{}");
            var profile = new ToolchainProfile(toolchain, "image", [], []);

            Assert.AreEqual(expected, CopilotRepairer.ShouldAttemptNpmAuditRepair(profile, logs, directory));
        }
        finally { Directory.Delete(directory, true); }
    }
}
