using System.Text;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies workflow fallback metadata and dependency review availability against exact PR revisions.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class WorkflowGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Reads action changes from exact workflow revisions when GitHub refuses dependency review.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnavailableDependencyReviewFallsBackToExactWorkflows()
    {
        var gateway = Gateway("[[{\"filename\":\".github/workflows/ci.yml\",\"status\":\"modified\"}]]", "gh: Forbidden (HTTP 403)");
        var pullRequest = TestData.PullRequest() with { BaseHead = "base-head" };
        var changes = await gateway.GetDependencyChangesAsync(pullRequest, testContext.CancellationToken);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Version == "v7.1.0", changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Version == "v7.0.0", changes);
    }

    /// <summary>
    /// Distinguishes disabled dependency review from transient failures when no fallback covers the changed files.
    /// </summary>
    /// <param name="error">The GitHub CLI error.</param>
    /// <param name="unavailable">Whether the repository configuration prevents verification.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("gh: Forbidden (HTTP 403)", true)]
    [DataRow("gh: Not Found (HTTP 404)", true)]
    [DataRow("gh: Server Error (HTTP 502)", false)]
    public async Task UncoveredChangesReportWhyReviewFailed(string error, bool unavailable)
    {
        var gateway = Gateway("[[{\"filename\":\"Cargo.lock\",\"status\":\"modified\"}]]", error);
        var exception = await Assert.ThrowsAsync<IOException>(() =>
            gateway.GetDependencyChangesAsync(TestData.PullRequest(), testContext.CancellationToken));
        Assert.AreEqual(unavailable, exception is DependencyReviewUnavailableException);
    }

    /// <summary>
    /// Compares the exact head against the live base tip.
    /// </summary>
    /// <param name="behind">GitHub's reported number of base commits missing from the head.</param>
    /// <param name="expected">Whether the head is behind.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("1", true)]
    [DataRow("0", false)]
    public async Task DetectsHeadsBehindTheLiveBase(string behind, bool expected)
    {
        var requests = new List<string>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            requests.Add(arguments[1]);
            return Task.FromResult(new CommandResult(0, behind + "\n", ""));
        });
        Assert.AreEqual(expected, await gateway.IsBehindAsync(TestData.PullRequest(), new string('c', 40),
            testContext.CancellationToken));
        Assert.AreEqual($"repos/owner/repository/compare/{new string('c', 40)}...{new string('a', 40)}", requests.Single());
    }

    private static GitHubGateway Gateway(string files, string reviewError) => new("", new Redactor(), (arguments, _, _) =>
    {
        var endpoint = arguments[1];
        if (endpoint.Contains("dependency-graph", StringComparison.Ordinal))
            return Task.FromResult(new CommandResult(1, "{\"message\":\"Forbidden\"}", reviewError));
        var output = endpoint.Contains("/pulls/1/files", StringComparison.Ordinal) ? files :
            Content(endpoint.Contains("ref=base-head", StringComparison.Ordinal) ?
                "    - uses: actions/setup-node@v7.0.0\n" : "    - uses: actions/setup-node@v7.1.0\n");
        return Task.FromResult(new CommandResult(0, output, ""));
    });

    private static string Content(string value) =>
        "{\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "\"}";
}
