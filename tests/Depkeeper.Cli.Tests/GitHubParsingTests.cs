using System.Text.Json;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies normalization of GitHub's polymorphic PR and check data.
/// </summary>
[TestClass]
public sealed class GitHubParsingTests
{
    /// <summary>
    /// Retains the server completion time needed by the stale-check gate.
    /// </summary>
    [TestMethod]
    public void ParsesCheckCompletionTimes()
    {
        using var document = JsonDocument.Parse("""
            {"number":1,"title":"Update","author":{"login":"app/dependabot"},
             "headRefName":"branch","headRefOid":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","baseRefName":"main",
             "isDraft":false,"isCrossRepository":false,"mergeable":"MERGEABLE","mergeStateStatus":"CLEAN",
             "reviewDecision":"","state":"OPEN","statusCheckRollup":[
               {"name":"build","status":"COMPLETED","conclusion":"SUCCESS",
                "detailsUrl":"https://github.com/owner/repository/actions/runs/1/job/2","completedAt":"2026-09-01T10:00:00Z"}
             ]}
            """);
        var pullRequest = GitHubGateway.Parse("owner/repository", document.RootElement);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-01T10:00:00Z"), pullRequest.Checks.Single().CompletedAt);
    }
}
