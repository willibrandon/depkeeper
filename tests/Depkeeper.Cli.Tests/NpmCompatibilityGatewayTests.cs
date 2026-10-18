using System.Text;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that compatibility evidence comes from the requested revision and excludes removed manifests.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class NpmCompatibilityGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Retrieves changed manifests at the supplied exact commit without falling back to a moving branch.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ReadsChangedManifestAtTheExactRequestedRevision()
    {
        var current = NpmCompatibilityFixture.PullRequest();
        var reads = 0;
        var gateway = new GitHubGateway("", new Redactor(), (arguments, _, _) =>
        {
            var endpoint = arguments[1];
            if (endpoint.Contains("/pulls/1/files", StringComparison.Ordinal))
                return Result("""
                    [[{"filename":"docs-site/package.json","status":"modified"},
                      {"filename":"removed/package.json","status":"removed"},
                      {"filename":"docs-site/package-lock.json","status":"modified"}]]
                    """);
            reads++;
            Assert.Contains("ref=" + current.Head, endpoint);
            Assert.Contains("docs-site%2Fpackage.json", endpoint);
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(NpmCompatibilityFixture.Manifest));
            return Result("{\"encoding\":\"base64\",\"content\":\"" + content + "\"}");
        });

        var manifests = await gateway.GetNpmManifestsAsync(current, testContext.CancellationToken);

        Assert.AreEqual(1, reads);
        Assert.AreEqual(NpmCompatibilityFixture.Manifest, manifests["docs-site/package.json"]);
        Assert.HasCount(1, manifests);
    }

    private static Task<CommandResult> Result(string json) => Task.FromResult(new CommandResult(0, json, ""));
}
