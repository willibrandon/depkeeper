namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies review-thread GraphQL parsing and exact-head resolution safeguards.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class ReviewGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Returns only unresolved conversations and preserves the latest comment identity.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ParsesUnresolvedReviewThreads()
    {
        var gateway = new GitHubGateway("", new Redactor(), (_, _, _) => Task.FromResult(new CommandResult(0, """
            [{"data":{"repository":{"pullRequest":{"reviewThreads":{"nodes":[
              {"id":"resolved","isResolved":true,"path":"src/old.cs","line":1,
               "comments":{"nodes":[{"id":"old","body":"Done","url":"old","author":{"login":"reviewer"}}],
                           "pageInfo":{"hasPreviousPage":false}}},
              {"id":"thread-1","isResolved":false,"path":"src/file.cs","line":12,
               "comments":{"nodes":[
                 {"id":"comment-1","body":"Please change this","url":"first","author":{"login":"reviewer"}},
                 {"id":"comment-2","body":"More detail","url":"latest","author":null}],
                 "pageInfo":{"hasPreviousPage":false}}}
            ],"pageInfo":{"hasNextPage":false,"endCursor":null}}}}}}]
            """, "")));
        var threads = await gateway.GetReviewThreadsAsync(TestData.PullRequest(), testContext.CancellationToken);
        var thread = threads.Single();
        Assert.AreEqual("thread-1", thread.Id);
        Assert.AreEqual("comment-2", thread.LatestCommentId);
        Assert.Contains("reviewer: Please change this", thread.Conversation);
        Assert.Contains("unknown: More detail", thread.Conversation);
    }

    /// <summary>
    /// Refuses to resolve a thread when the PR head changed after validation.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task HeadChangePreventsThreadResolution()
    {
        var mutations = 0;
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            if (arguments[0] == "pr") return Task.FromResult(new CommandResult(0, PullRequestJson(new string('b', 40)), ""));
            mutations++;
            return Task.FromResult(new CommandResult(0, "{}", ""));
        });
        await Assert.ThrowsAsync<IOException>(() => gateway.ResolveReviewThreadsAsync(TestData.PullRequest(), ["thread-1"],
            testContext.CancellationToken));
        Assert.AreEqual(0, mutations);
    }

    private static string PullRequestJson(string head) => $$"""
        {"number":1,"title":"Update","author":{"login":"app/dependabot"},"headRefName":"branch",
         "headRefOid":"{{head}}","baseRefName":"main","isDraft":false,"isCrossRepository":false,
         "mergeable":"MERGEABLE","mergeStateStatus":"CLEAN","reviewDecision":"","state":"OPEN","statusCheckRollup":[]}
        """;
}
