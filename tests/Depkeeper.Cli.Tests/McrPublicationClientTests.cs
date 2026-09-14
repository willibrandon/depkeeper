namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies exact Microsoft Container Registry digest matching before trusting catalog timestamps.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class McrPublicationClientTests(TestContext testContext)
{
    /// <summary>
    /// Returns MCR's tag update timestamp only for the exact digest.
    /// </summary>
    /// <param name="matches">Whether MCR reports the requested digest.</param>
    /// <param name="found">Whether a publication timestamp is expected.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public async Task RequiresExactMcrDigestMatch(bool matches, bool found)
    {
        var expected = new string('a', 64);
        var reported = matches ? expected : new string('b', 64);
        var handler = new TestHttpMessageHandler(request =>
        {
            Assert.AreEqual("mcr.microsoft.com", request.RequestUri!.Host);
            Assert.Contains("/api/v1/catalog/devcontainers/base/tags", request.RequestUri.AbsolutePath);
            return TestHttpMessageHandler.Json($$"""
                [{"name":"2-trixie","digest":"sha256:{{reported}}","lastModifiedDate":"2026-09-10T12:59:06Z"}]
                """);
        });
        using var http = new HttpClient(handler);
        var client = new McrPublicationClient(http);
        var dependency = new DependencyChange("added", "docker", "mcr.microsoft.com/devcontainers/base",
            "2-trixie@sha256:" + expected, []);
        var published = await client.GetAsync(dependency, testContext.CancellationToken);
        Assert.AreEqual(found, published is not null);
        if (found) Assert.AreEqual(DateTimeOffset.Parse("2026-09-10T12:59:06Z"), published);
    }

    /// <summary>
    /// Does not query MCR for images hosted elsewhere.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RefusesOtherRegistriesWithoutNetworkAccess()
    {
        var handler = new TestHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected request."));
        using var http = new HttpClient(handler);
        var client = new McrPublicationClient(http);
        var dependency = new DependencyChange("added", "docker", "docker.io/library/docker",
            "29.7.2-cli@sha256:" + new string('a', 64), []);
        Assert.IsNull(await client.GetAsync(dependency, testContext.CancellationToken));
        Assert.AreEqual(0, handler.Requests);
    }
}
