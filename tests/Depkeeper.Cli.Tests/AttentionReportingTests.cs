namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that issue reporting is restricted to actionable problems and managed issue lifecycles.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class AttentionReportingTests(TestContext testContext)
{
    /// <summary>
    /// Keeps routine outcomes out of GitHub issues entirely.
    /// </summary>
    /// <param name="outcome">The non-actionable maintenance outcome.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("pending")]
    [DataRow("queued")]
    [DataRow("cooldown")]
    [DataRow("clear")]
    [DataRow("deferred")]
    public async Task RoutineOutcomesNeverCallTheIssuesApi(string outcome)
    {
        var operations = new List<string>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            operations.Add(string.Join(' ', arguments));
            return Task.FromResult(new CommandResult(0, "[]", ""));
        });
        await gateway.PublishAttentionAsync(new ReportEntry("owner/repository", 1, outcome, "Routine result."),
            "auto", null, testContext.CancellationToken);
        Assert.IsEmpty(operations);
    }

    /// <summary>
    /// Creates a redacted blocker issue in the affected repository while successful runs create none.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task OnlyBlockersCreateIssuesInTheAffectedRepository()
    {
        var operations = new List<IReadOnlyList<string>>();
        var bodies = new List<string>();
        var gateway = new GitHubGateway("", new Redactor("example-credential"), (arguments, _, input) =>
        {
            operations.Add(arguments);
            if (input is not null) bodies.Add(input);
            return Task.FromResult(new CommandResult(0, "[]", ""));
        });
        await gateway.PublishAttentionAsync(new ReportEntry("owner/repository", 1, "merged", "Verified merge."),
            "auto", null, testContext.CancellationToken);
        Assert.IsEmpty(bodies);
        await gateway.PublishAttentionAsync(new ReportEntry("owner/repository", 2, "blocked", "Failed: example-credential"),
            "auto", null, testContext.CancellationToken);
        var create = operations.Single(arguments => arguments[1] == "create");
        Assert.Contains("owner/repository", create);
        Assert.Contains("depkeeper:owner/repository:2", bodies.Single());
        Assert.DoesNotContain("example-credential", bodies.Single());
    }

    /// <summary>
    /// Closes only the matching managed issue after the affected PR is independently merged.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task VerifiedMergeClosesItsManagedIssue()
    {
        var operations = new List<IReadOnlyList<string>>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            operations.Add(arguments);
            return Task.FromResult(new CommandResult(0, """
                [{"number":5,"title":"Depkeeper needs attention: owner/repository#1",
                  "body":"<!-- depkeeper:owner/repository:1 -->"}]
                """, ""));
        });
        await gateway.PublishAttentionAsync(new ReportEntry("owner/repository", 1, "merged", "Verified merge."),
            "auto", null, testContext.CancellationToken);
        var close = operations.Single(arguments => arguments[1] == "close");
        Assert.Contains("5", close);
        Assert.Contains("completed", close);
    }
}
