namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies which branches Dependabot may rebase without discarding controller or human work.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class DependabotOwnershipTests(TestContext testContext)
{
    /// <summary>
    /// Accepts Dependabot commits and merges from the base, and rejects repair commits, foreign commits, and empty branches.
    /// </summary>
    /// <param name="commits">The commits GitHub reports for the branch.</param>
    /// <param name="expected">Whether the branch is Dependabot-owned.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("""[{"author":{"login":"dependabot[bot]"},"parents":[{}]}]""", true)]
    [DataRow("""[{"author":{"login":"dependabot[bot]"},"parents":[{}]},{"author":{"login":"willibrandon"},"parents":[{},{}]}]""", true)]
    [DataRow("""[{"author":{"login":"dependabot[bot]"},"parents":[{}]},{"author":{"login":"github-actions[bot]"},"parents":[{}]}]""",
        false)]
    [DataRow("""[{"author":null,"parents":[{}]}]""", false)]
    [DataRow("[]", false)]
    public async Task RecognizesDependabotOwnedBranches(string commits, bool expected)
    {
        var requests = new List<string>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            requests.Add(arguments[1]);
            return Task.FromResult(new CommandResult(0, "[" + commits + "]", ""));
        });
        Assert.AreEqual(expected, await gateway.IsDependabotOwnedAsync(TestData.PullRequest(), testContext.CancellationToken));
        Assert.ContainsSingle(endpoint => endpoint.StartsWith("repos/owner/repository/pulls/1/commits", StringComparison.Ordinal),
            requests);
    }
}
