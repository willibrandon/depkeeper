namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies exact Docker Hub digest matching before registry timestamps are trusted.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class DockerHubPublicationClientTests(TestContext testContext)
{
    /// <summary>
    /// Returns the tag push timestamp only for an exact full digest match.
    /// </summary>
    /// <param name="matches">Whether Docker Hub reports the requested digest.</param>
    /// <param name="found">Whether a publication timestamp is expected.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public async Task RequiresExactDigestMatch(bool matches, bool found)
    {
        var expected = new string('a', 64);
        var reported = matches ? expected : new string('b', 64);
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json($$"""
            {"digest":"sha256:{{reported}}","tag_last_pushed":"2026-08-31T22:08:31Z"}
            """));
        using var http = new HttpClient(handler);
        var client = new DockerHubPublicationClient(http);
        var dependency = new DependencyChange("added", "docker", "docker.io/library/docker",
            "29.7.2-cli@sha256:" + expected, []);
        var published = await client.GetAsync(dependency, testContext.CancellationToken);
        Assert.AreEqual(found, published is not null);
        if (found) Assert.AreEqual(DateTimeOffset.Parse("2026-08-31T22:08:31Z"), published);
    }

    /// <summary>
    /// Refuses unsupported registries without issuing a misleading Docker Hub request.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task DoesNotMapMcrDigestsToDockerHub()
    {
        var handler = new TestHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected HTTP request."));
        using var http = new HttpClient(handler);
        var client = new DockerHubPublicationClient(http);
        var dependency = new DependencyChange("added", "docker", "mcr.microsoft.com/devcontainers/base",
            "2-trixie@sha256:" + new string('a', 64), []);
        Assert.IsNull(await client.GetAsync(dependency, testContext.CancellationToken));
        Assert.AreEqual(0, handler.Requests);
    }
}
