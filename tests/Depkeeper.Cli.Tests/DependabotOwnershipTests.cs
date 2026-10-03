namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies which branches Dependabot may rebase without discarding controller or human work.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class DependabotOwnershipTests(TestContext testContext)
{
    /// <summary>
    /// Separates pristine Dependabot branches, branches with merges from the base, and branches carrying foreign commits.
    /// </summary>
    /// <param name="commits">The commits GitHub reports for the branch.</param>
    /// <param name="expected">The expected ownership name.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("""[{"author":{"login":"dependabot[bot]"},"parents":[{}]}]""", "Dependabot")]
    [DataRow("""[{"author":{"login":"dependabot[bot]"},"parents":[{},{}]}]""", "Dependabot")]
    [DataRow("""[{"author":{"login":"dependabot[bot]"},"parents":[{}]},{"author":{"login":"willibrandon"},"parents":[{},{}]}]""",
        "DependabotWithMerges")]
    [DataRow("""[{"author":{"login":"dependabot[bot]"},"parents":[{}]},{"author":{"login":"github-actions[bot]"},"parents":[{}]}]""",
        "Foreign")]
    [DataRow("""[{"author":null,"parents":[{}]}]""", "Foreign")]
    [DataRow("[]", "Foreign")]
    public async Task ClassifiesBranchOwnership(string commits, string expected)
    {
        var requests = new List<string>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            requests.Add(arguments[1]);
            return Task.FromResult(new CommandResult(0, "[" + commits + "]", ""));
        });
        Assert.AreEqual(Enum.Parse<BranchOwnership>(expected),
            await gateway.GetBranchOwnershipAsync(TestData.PullRequest(), testContext.CancellationToken));
        Assert.ContainsSingle(endpoint => endpoint.StartsWith("repos/owner/repository/pulls/1/commits", StringComparison.Ordinal),
            requests);
    }
}
