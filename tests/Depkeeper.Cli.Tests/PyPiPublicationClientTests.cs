using System.Net;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies exact PyPI release identity and artifact upload timestamps.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class PyPiPublicationClientTests(TestContext testContext)
{
    /// <summary>
    /// Uses the earliest artifact upload after matching a canonical package name and exact version.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ReturnsEarliestExactReleaseUpload()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            Assert.AreEqual("pypi.org", request.RequestUri!.Host);
            return TestHttpMessageHandler.Json("""
                {
                  "info":{"name":"Example.Package","version":"1.2.3"},
                  "urls":[
                    {"upload_time_iso_8601":"2026-09-10T07:56:00Z"},
                    {"upload_time_iso_8601":"2026-09-10T07:55:59Z"}
                  ]
                }
                """);
        });
        using var http = new HttpClient(handler);
        var client = new PyPiPublicationClient(http);
        var dependency = new DependencyChange("added", "pip", "example-package", "1.2.3", []);
        var published = await client.GetAsync(dependency, testContext.CancellationToken);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-10T07:55:59Z"), published);
    }

    /// <summary>
    /// Rejects metadata for a different package or release.
    /// </summary>
    /// <param name="name">The package name reported by PyPI.</param>
    /// <param name="version">The package version reported by PyPI.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("other", "1.2.3")]
    [DataRow("example-package", "1.2.4")]
    public async Task RejectsMismatchedRelease(string name, string version)
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json($$"""
            {"info":{"name":"{{name}}","version":"{{version}}"},"urls":[{"upload_time_iso_8601":"2026-09-10T07:55:59Z"}]}
            """));
        using var http = new HttpClient(handler);
        var client = new PyPiPublicationClient(http);
        var dependency = new DependencyChange("added", "pypi", "example-package", "1.2.3", []);
        Assert.IsNull(await client.GetAsync(dependency, testContext.CancellationToken));
    }

    /// <summary>
    /// Falls back to PyPI when deps.dev has not ingested an exact Python release.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task PublicationClientFallsBackAfterDepsDevNotFound()
    {
        var handler = new TestHttpMessageHandler(request => request.RequestUri!.Host == "api.deps.dev"
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : TestHttpMessageHandler.Json("""
                {"info":{"name":"build","version":"1.6.1"},"urls":[{"upload_time_iso_8601":"2026-09-10T07:55:59Z"}]}
                """));
        using var http = new HttpClient(handler);
        var pypi = new PyPiPublicationClient(http);
        var client = new PublicationClient(http, pypi: pypi.GetAsync);
        var dependency = new DependencyChange("added", "pip", "build", "1.6.1", []);
        var published = await client.GetAsync(dependency, testContext.CancellationToken);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-10T07:55:59Z"), published);
        Assert.AreEqual(2, handler.Requests);
    }
}
