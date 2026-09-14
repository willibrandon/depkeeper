namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that repair evidence includes installation context from the specific failing job.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class FailureLogTests(TestContext testContext)
{
    /// <summary>
    /// Retrieves full failing-job logs and redacts them before providing them to the repairer.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task IncludesInstallationStepsFromTheFailingJob()
    {
        IReadOnlyList<string>? request = null;
        var gateway = new GitHubGateway("", new Redactor("example-credential"), (arguments, _, _) =>
        {
            request = arguments;
            return Task.FromResult(new CommandResult(0, "npm ci succeeded\nTest startup failed\nexample-credential", ""));
        });
        var pr = TestData.PullRequest() with
        {
            Checks = [new CheckSnapshot("tests", "FAILURE", "https://github.com/owner/repository/actions/runs/123/job/456")]
        };
        var logs = await gateway.GetFailureLogsAsync(pr, testContext.CancellationToken);
        Assert.IsNotNull(request);
        Assert.Contains("--job", request);
        Assert.Contains("456", request);
        Assert.Contains("--log", request);
        Assert.Contains("npm ci succeeded", logs);
        Assert.DoesNotContain("example-credential", logs);
    }
}
