namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies exact-commit queries and waiting for complete workflows rather than only their earliest jobs.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class CommitChecksTests(TestContext testContext)
{
    /// <summary>
    /// Requires GitHub to confirm ancestry before using a later base commit as a follow-up fix.
    /// </summary>
    /// <param name="comparison">GitHub's comparison result.</param>
    /// <param name="accepted">Whether the later commit is a verified descendant.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("ahead", true)]
    [DataRow("behind", false)]
    [DataRow("diverged", false)]
    public async Task RecoveryRequiresConfirmedAncestry(string comparison, bool accepted)
    {
        var ancestor = new string('c', 40);
        var descendant = new string('d', 40);
        var requests = new List<string>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            requests.Add(arguments[1]);
            return Task.FromResult(new CommandResult(0,
                arguments[1].Contains("/compare/", StringComparison.Ordinal) ? comparison : descendant, ""));
        });
        var result = await gateway.GetBranchDescendantAsync("owner/repository", "main", ancestor, testContext.CancellationToken);
        Assert.AreEqual(accepted ? descendant : null, result);
        Assert.Contains("repos/owner/repository/compare/" + ancestor + "..." + descendant, requests);
    }

    /// <summary>
    /// Does not mistake dependency-update housekeeping for a successful CI run.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task DependabotMetadataChecksAreNotVerification()
    {
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            var body = arguments[1].Contains("check-runs", StringComparison.Ordinal) ? """
                [{"check_runs":[{"name":"Dependabot","status":"completed","conclusion":"success","app":{"slug":"dependabot"}}]}]
                """ : arguments[1].Contains("/status?", StringComparison.Ordinal) ? """
                [{"statuses":[]}]
                """ : """
                [{"workflow_runs":[]}]
                """;
            return Task.FromResult(new CommandResult(0, body, ""));
        });
        var checks = await gateway.GetCommitChecksAsync("owner/repository", new string('c', 40), testContext.CancellationToken);
        Assert.IsEmpty(checks);
        Assert.IsNotNull(MergePolicy.GetChecksBlocker(checks, new RepositoryProfile()));
    }

    /// <summary>
    /// Keeps the commit pending when an overall workflow is still running despite a successful initial job.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ReadsTheExactCommitAndTracksWorkflowCompletion()
    {
        var commit = new string('c', 40);
        var requests = new List<string>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            requests.Add(arguments[1]);
            var body = arguments[1].Contains("check-runs", StringComparison.Ordinal) ? """
                [{"check_runs":[{"name":"build","status":"completed","conclusion":"success"}]}]
                """ : arguments[1].Contains("/status?", StringComparison.Ordinal) ? """
                [{"statuses":[{"context":"external","state":"success"}]}]
                """ : """
                [{"workflow_runs":[
                  {"id":1,"workflow_id":10,"name":"CI","event":"push","status":"completed","conclusion":"success"},
                  {"id":2,"workflow_id":10,"name":"CI","event":"push","status":"in_progress","conclusion":null}
                ]}]
                """;
            return Task.FromResult(new CommandResult(0, body, ""));
        });
        var checks = await gateway.GetCommitChecksAsync("owner/repository", commit, testContext.CancellationToken);
        Assert.IsTrue(requests.All(request => request.Contains(commit, StringComparison.Ordinal)));
        Assert.AreEqual("IN_PROGRESS", checks.Single(check => check.Name == "workflow: CI").State);
        Assert.IsNotNull(MergePolicy.GetChecksBlocker(checks, new RepositoryProfile()));
    }
}
