namespace Depkeeper.Cli.Tests;

/// <summary>
/// Ensures a check URL cannot cause a workflow on a different repository or revision to be rerun.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class CheckRefreshGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Requires both the configured repository and exact commit before requesting a workflow rerun.
    /// </summary>
    /// <param name="urlRepository">The repository referenced by the check URL.</param>
    /// <param name="sameHead">Whether GitHub confirms the expected commit.</param>
    /// <param name="accepted">Whether a rerun is permitted.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("owner/repository", true, true)]
    [DataRow("owner/repository", false, false)]
    [DataRow("owner/other", true, false)]
    public async Task RefreshIsBoundToTheInspectedCommit(string urlRepository, bool sameHead, bool accepted)
    {
        var head = new string('a', 40);
        var operations = new List<IReadOnlyList<string>>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            operations.Add(arguments);
            return Task.FromResult(new CommandResult(0, sameHead ? head : new string('b', 40), ""));
        });
        var result = await gateway.RerunChecksAsync("owner/repository", head,
            [new CheckSnapshot("build", "SUCCESS", $"https://github.com/{urlRepository}/actions/runs/123/job/456")],
            false, testContext.CancellationToken);
        Assert.AreEqual(accepted, result);
        Assert.AreEqual(accepted, operations.Any(arguments => arguments[1] == "rerun"));
    }
}
