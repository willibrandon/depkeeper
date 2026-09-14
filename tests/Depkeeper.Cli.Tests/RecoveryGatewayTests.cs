namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies recovery PR ownership, idempotency, assignment, and label metadata at the GitHub boundary.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class RecoveryGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Uses authenticated authorship and exact branch identity instead of trusting PR text.
    /// </summary>
    /// <param name="author">The author returned by GitHub.</param>
    /// <param name="accepted">Whether the PR belongs to the controller account.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("maintainer", true)]
    [DataRow("another-user", false)]
    public async Task RecoveryOwnershipMustBeVerified(string author, bool accepted)
    {
        var request = Request();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) => Task.FromResult(new CommandResult(0,
            arguments[0] == "api" ? "maintainer" : PullRequestJson(author, request.Branch), "")));
        if (!accepted)
        {
            await Assert.ThrowsAsync<IOException>(() => gateway.FindRecoveryAsync(request, testContext.CancellationToken));
            return;
        }
        var found = await gateway.FindRecoveryAsync(request, testContext.CancellationToken);
        Assert.IsNotNull(found);
        Assert.IsTrue(found.ManagedRecovery);
    }

    /// <summary>
    /// Creates one assigned PR and adds complete metadata when the bug label is absent.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RepeatedCreationResumesTheSameAssignedAndLabeledPr()
    {
        var request = Request();
        var created = false;
        var operations = new List<IReadOnlyList<string>>();
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            operations.Add(arguments);
            if (arguments[0] == "api") return Task.FromResult(new CommandResult(0, "maintainer", ""));
            if (arguments[0] == "pr" && arguments[1] == "list")
                return Task.FromResult(new CommandResult(0, created ? PullRequestJson("maintainer", request.Branch) : "[]", ""));
            if (arguments[0] == "pr" && arguments[1] == "create") created = true;
            return Task.FromResult(new CommandResult(0, "[]", ""));
        });
        var first = await gateway.CreateRecoveryPullRequestAsync(request, new RepositoryProfile(), testContext.CancellationToken);
        var second = await gateway.CreateRecoveryPullRequestAsync(request, new RepositoryProfile(), testContext.CancellationToken);
        Assert.AreEqual(first.Number, second.Number);
        var create = operations.Single(arguments => arguments[0] == "pr" && arguments[1] == "create");
        Assert.Contains("@me", create);
        Assert.Contains("bug", create);
        var label = operations.Single(arguments => arguments[0] == "label" && arguments[1] == "create");
        Assert.Contains("d73a4a", label);
        Assert.Contains("Something isn't working", label);
    }

    private static RecoveryRequest Request() => new("owner/repository", 1, "main", new string('c', 40),
        "depkeeper/repair-" + new string('c', 40));

    private static string PullRequestJson(string author, string branch) => $$"""
        [{"number":1001,"title":"Repair CI","author":{"login":"{{author}}"},
          "headRefName":"{{branch}}","headRefOid":"dddddddddddddddddddddddddddddddddddddddd",
          "baseRefName":"main","isDraft":false,"isCrossRepository":false,"state":"OPEN"}]
        """;
}
