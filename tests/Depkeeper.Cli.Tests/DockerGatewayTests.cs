using System.Text;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies Dockerfile fallback metadata against the exact PR base and head revisions.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class DockerGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Adds Docker changes when GitHub dependency review returns no records.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task EmptyDependencyReviewFallsBackToExactDockerfiles()
    {
        var oldDigest = new string('a', 64);
        var newDigest = new string('b', 64);
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            var endpoint = arguments[1];
            var output = endpoint.Contains("dependency-graph", StringComparison.Ordinal) ? "[]" :
                endpoint.Contains("/pulls/1/files", StringComparison.Ordinal) ? "[[{\"filename\":\".devcontainer/Dockerfile\"}]]" :
                Content(endpoint.Contains("ref=base-head", StringComparison.Ordinal) ?
                    $"FROM docker:tag@sha256:{oldDigest}\n" : $"FROM docker:tag@sha256:{newDigest}\n");
            return Task.FromResult(new CommandResult(0, output, ""));
        });
        var pullRequest = TestData.PullRequest() with { BaseHead = "base-head" };
        var changes = await gateway.GetDependencyChangesAsync(pullRequest, testContext.CancellationToken);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Version.EndsWith(newDigest, StringComparison.Ordinal),
            changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Version.EndsWith(oldDigest, StringComparison.Ordinal),
            changes);
    }

    private static string Content(string value) =>
        "{\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "\"}";
}
