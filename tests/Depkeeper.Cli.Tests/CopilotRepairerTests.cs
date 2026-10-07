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
    /// <param name="currentFailures">The current failed-check evidence.</param>
    /// <param name="context">Review or historical context supplied to the repair agent.</param>
    /// <param name="expected">Whether deterministic remediation is eligible.</param>
    [TestMethod]
    [DataRow("node", true, "# npm audit report", "", true)]
    [DataRow("node", true, "npm audit --audit-level=moderate", "", false)]
    [DataRow("node", false, "# npm audit report", "", false)]
    [DataRow("node", true, "TypeScript compilation failed", "", false)]
    [DataRow("dotnet", true, "# npm audit report", "", false)]
    [DataRow("node", true, "VS Code integration test was unresponsive", "Previous failure: # npm audit report", false)]
    public void SelectsOnlyCurrentNpmAuditFailures(string toolchain, bool hasLock, string currentFailures, string context,
        bool expected)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-repair-").FullName;
        try
        {
            if (hasLock) File.WriteAllText(Path.Join(directory, "package-lock.json"), "{}");
            var profile = new ToolchainProfile(toolchain, "image", [], []);
            var evidence = new RepairEvidence(currentFailures, context);

            Assert.AreEqual(expected, CopilotRepairer.ShouldAttemptNpmAuditRepair(profile, evidence, directory));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Falls through to Copilot when npm exits successfully without changing the checkout.
    /// </summary>
    [TestMethod]
    public void SuccessfulNoOpAuditRepairIsNotTreatedAsARepair()
    {
        var result = new CommandResult(0, "found 0 vulnerabilities", "");

        Assert.IsFalse(CopilotRepairer.ShouldUseNpmAuditRepairResult(result, []));
        Assert.IsTrue(CopilotRepairer.ShouldUseNpmAuditRepairResult(result, ["package-lock.json"]));
    }
}
