namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that Go module versions reported by GitHub are looked up in the form the Go ecosystem publishes.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class GoPublicationTests(TestContext testContext)
{
    /// <summary>
    /// Adds the leading v that dependency review strips, and leaves already prefixed versions and other systems alone.
    /// </summary>
    /// <param name="ecosystem">GitHub's reported ecosystem.</param>
    /// <param name="version">GitHub's reported version.</param>
    /// <param name="expected">The version segment deps.dev must receive.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("gomod", "2.0.10", "v2.0.10")]
    [DataRow("go", "0.0.0-20260924080639-d5bfd5c2ff74", "v0.0.0-20260924080639-d5bfd5c2ff74")]
    [DataRow("gomod", "v2.0.10", "v2.0.10")]
    [DataRow("npm", "2.0.10", "2.0.10")]
    public async Task GoVersionsCarryTheLeadingV(string ecosystem, string version, string expected)
    {
        string? requested = null;
        var handler = new TestHttpMessageHandler(request =>
        {
            requested = request.RequestUri!.AbsolutePath;
            return TestHttpMessageHandler.Json("""{"publishedAt":"2026-09-24T08:06:39Z"}""");
        });
        using var http = new HttpClient(handler);
        var client = new PublicationClient(http);
        var dependency = new DependencyChange("added", ecosystem, "charm.land/bubbletea/v2", version, []);
        var published = await client.GetAsync(dependency, testContext.CancellationToken);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-24T08:06:39Z"), published);
        Assert.EndsWith("/versions/" + expected, requested);
        Assert.Contains("/packages/charm.land%2Fbubbletea%2Fv2/", requested);
    }
}
