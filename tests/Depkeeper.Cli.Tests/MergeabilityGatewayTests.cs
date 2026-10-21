namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that unknown GraphQL mergeability is resolved through GitHub's REST calculation for the exact head.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class MergeabilityGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Adopts REST mergeability only when it is computed for the same head GraphQL reported.
    /// </summary>
    /// <param name="sha">The head reported by the REST resource.</param>
    /// <param name="mergeable">The REST mergeable value.</param>
    /// <param name="state">The REST mergeable state.</param>
    /// <param name="expectedMergeable">The expected conflict assessment.</param>
    /// <param name="expectedState">The expected merge gate.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow('a', "false", "dirty", "CONFLICTING", "DIRTY")]
    [DataRow('a', "true", "clean", "MERGEABLE", "CLEAN")]
    [DataRow('a', "null", "unknown", "UNKNOWN", "UNKNOWN")]
    [DataRow('b', "false", "dirty", "UNKNOWN", "UNKNOWN")]
    public async Task UnknownMergeabilityIsReadFromTheRestResource(char sha, string mergeable, string state,
        string expectedMergeable, string expectedState)
    {
        var requests = new List<string>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            requests.Add(arguments[1]);
            return Task.FromResult(new CommandResult(0, arguments[0] == "pr" ? View("UNKNOWN") :
                $"{{\"head\":{{\"sha\":\"{new string(sha, 40)}\"}},\"mergeable\":{mergeable},\"mergeable_state\":\"{state}\"}}", ""));
        });
        var current = await gateway.RefreshAsync(TestData.PullRequest(), testContext.CancellationToken);
        Assert.AreEqual(expectedMergeable, current.Mergeable);
        Assert.AreEqual(expectedState, current.MergeState);
        Assert.AreEqual("repos/owner/repository/pulls/1", requests[1]);
    }

    /// <summary>
    /// Leaves known GraphQL mergeability untouched without another request.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task KnownMergeabilityNeedsNoRestRequest()
    {
        var requests = 0;
        var gateway = new GitHubGateway("", new Redactor(), (_, _, _) =>
        {
            requests++;
            return Task.FromResult(new CommandResult(0, View("CONFLICTING"), ""));
        });
        var current = await gateway.RefreshAsync(TestData.PullRequest(), testContext.CancellationToken);
        Assert.AreEqual("CONFLICTING", current.Mergeable);
        Assert.AreEqual(1, requests);
    }

    private static string View(string mergeable) =>
        $"{{\"number\":1,\"headRefOid\":\"{new string('a', 40)}\",\"isDraft\":false,\"isCrossRepository\":false," +
        $"\"mergeable\":\"{mergeable}\",\"mergeStateStatus\":\"{(mergeable == "UNKNOWN" ? "UNKNOWN" : "DIRTY")}\"}}";
}
