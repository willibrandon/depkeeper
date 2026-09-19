using System.Net;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies checksum-exact Hex release identity and registry publication timestamps.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class HexPublicationClientTests(TestContext testContext)
{
    private static readonly string Locked = MixLockFixture.Checksum('a');

    /// <summary>
    /// Uses the registry insertion time after matching the exact version and locked checksum.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ReturnsInsertionTimeForExactChecksum()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            Assert.AreEqual("https://hex.pm/api/packages/mint/releases/1.10.1", request.RequestUri!.AbsoluteUri);
            return TestHttpMessageHandler.Json(Release("1.10.1", Locked.ToUpperInvariant()));
        });
        using var http = new HttpClient(handler);
        var client = new HexPublicationClient(http);
        var published = await client.GetAsync(Dependency(), testContext.CancellationToken);
        Assert.AreEqual(DateTimeOffset.Parse("2026-09-10T07:55:59.393282Z"), published);
    }

    /// <summary>
    /// Rejects another release, other content, retired releases, and records without a usable timestamp.
    /// </summary>
    /// <param name="version">The version reported by hex.pm.</param>
    /// <param name="checksum">The checksum reported by hex.pm.</param>
    /// <param name="retirement">The retirement JSON reported by hex.pm, or null to omit the property.</param>
    /// <param name="inserted">The insertion timestamp JSON reported by hex.pm.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("1.10.2", "a", "null", "\"2026-09-10T07:55:59Z\"")]
    [DataRow("1.10.1", "b", "null", "\"2026-09-10T07:55:59Z\"")]
    [DataRow("1.10.1", "a", "{\"reason\":\"security\",\"message\":\"CVE\"}", "\"2026-09-10T07:55:59Z\"")]
    [DataRow("1.10.1", "a", null, "\"2026-09-10T07:55:59Z\"")]
    [DataRow("1.10.1", "a", "null", "\"yesterday\"")]
    [DataRow("1.10.1", "a", "null", "null")]
    public async Task RejectsMismatchedRetiredOrUndatedReleases(string version, string checksum, string? retirement, string inserted)
    {
        var body = "{\"version\":\"" + version + "\",\"checksum\":\"" + MixLockFixture.Checksum(checksum[0]) + "\"," +
            (retirement is null ? string.Empty : "\"retirement\":" + retirement + ",") + "\"inserted_at\":" + inserted + "}";
        using var http = new HttpClient(new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(body)));
        var client = new HexPublicationClient(http);
        Assert.IsNull(await client.GetAsync(Dependency(), testContext.CancellationToken));
    }

    /// <summary>
    /// Rejects records whose identifying fields are missing or have an unexpected JSON type.
    /// </summary>
    /// <param name="body">The malformed registry response.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"version\":1,\"checksum\":\"x\",\"retirement\":null}")]
    [DataRow("{\"version\":\"1.10.1\",\"checksum\":null,\"retirement\":null,\"inserted_at\":\"2026-09-10T07:55:59Z\"}")]
    [DataRow("{\"version\":\"1.10.1\",\"retirement\":null,\"inserted_at\":\"2026-09-10T07:55:59Z\"}")]
    public async Task RejectsMalformedRecords(string body)
    {
        using var http = new HttpClient(new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(body)));
        var client = new HexPublicationClient(http);
        Assert.IsNull(await client.GetAsync(Dependency(), testContext.CancellationToken));
    }

    /// <summary>
    /// Never contacts hex.pm for records it could not verify: other ecosystems, unlocked checksums, and private repositories.
    /// </summary>
    /// <param name="ecosystem">The dependency ecosystem.</param>
    /// <param name="name">The dependency name.</param>
    /// <param name="locked">Whether the lock recorded a checksum.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("npm", "mint", true)]
    [DataRow("git", "https://github.com/example/library.git", false)]
    [DataRow("hex", "mint", false)]
    [DataRow("hex", "hexpm:acme/internal_tool", true)]
    [DataRow("hex", "dependabot/jason", true)]
    [DataRow("hex", "../admin", true)]
    public async Task SkipsRegistryForUnverifiableRecords(string ecosystem, string name, bool locked)
    {
        var handler = new TestHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected registry request."));
        using var http = new HttpClient(handler);
        var client = new HexPublicationClient(http);
        var dependency = new DependencyChange("added", ecosystem, name, "1.0.0", [], locked ? Locked : null);
        Assert.IsNull(await client.GetAsync(dependency, testContext.CancellationToken));
        Assert.AreEqual(0, handler.Requests);
    }

    /// <summary>
    /// Holds unknown releases and remembers each verified answer for the rest of the sweep.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task CachesMissingAndVerifiedReleases()
    {
        var handler = new TestHttpMessageHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/9.9.9", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : TestHttpMessageHandler.Json(Release("1.10.1", Locked)));
        using var http = new HttpClient(handler);
        var client = new HexPublicationClient(http);
        var missing = Dependency() with { Version = "9.9.9" };
        Assert.IsNull(await client.GetAsync(missing, testContext.CancellationToken));
        Assert.IsNull(await client.GetAsync(missing, testContext.CancellationToken));
        Assert.IsNotNull(await client.GetAsync(Dependency(), testContext.CancellationToken));
        Assert.IsNotNull(await client.GetAsync(Dependency(), testContext.CancellationToken));
        Assert.AreEqual(2, handler.Requests);
    }

    /// <summary>
    /// Verifies a different locked checksum separately instead of reusing another checksum's answer.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task DoesNotShareCachedAnswersAcrossChecksums()
    {
        var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(Release("1.10.1", Locked)));
        using var http = new HttpClient(handler);
        var client = new HexPublicationClient(http);
        Assert.IsNotNull(await client.GetAsync(Dependency(), testContext.CancellationToken));
        var tampered = Dependency() with { Checksum = MixLockFixture.Checksum('b') };
        Assert.IsNull(await client.GetAsync(tampered, testContext.CancellationToken));
        Assert.AreEqual(2, handler.Requests);
    }

    /// <summary>
    /// Surfaces registry failures and rate limits so the age gate holds the update.
    /// </summary>
    /// <param name="status">The failing registry status.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.InternalServerError)]
    public async Task RegistryFailuresPropagateToTheGate(HttpStatusCode status)
    {
        using var http = new HttpClient(new TestHttpMessageHandler(_ => new HttpResponseMessage(status)));
        var client = new HexPublicationClient(http);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(Dependency(), testContext.CancellationToken));
    }

    /// <summary>
    /// Routes Hex records to hex.pm rather than deps.dev, and holds them when no Hex lookup is configured.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task PublicationClientRoutesHexToItsRegistry()
    {
        var handler = new TestHttpMessageHandler(request =>
        {
            Assert.AreEqual("hex.pm", request.RequestUri!.Host);
            return TestHttpMessageHandler.Json(Release("1.10.1", Locked));
        });
        using var http = new HttpClient(handler);
        var hex = new HexPublicationClient(http);
        var routed = new PublicationClient(http, hex: hex.GetAsync);
        Assert.IsNotNull(await routed.GetAsync(Dependency(), testContext.CancellationToken));
        Assert.AreEqual(1, handler.Requests);
        var unconfigured = new PublicationClient(http);
        Assert.IsNull(await unconfigured.GetAsync(Dependency(), testContext.CancellationToken));
        var git = new DependencyChange("added", "git", "https://github.com/example/library.git", new string('a', 40), []);
        Assert.IsNull(await routed.GetAsync(git, testContext.CancellationToken));
        Assert.AreEqual(1, handler.Requests);
    }

    private static DependencyChange Dependency() => new("added", "hex", "mint", "1.10.1", [], Locked);

    private static string Release(string version, string checksum) =>
        "{\"version\":\"" + version + "\",\"checksum\":\"" + checksum + "\",\"retirement\":null," +
        "\"inserted_at\":\"2026-09-10T07:55:59.393282Z\",\"updated_at\":\"2026-09-18T00:00:00Z\"}";
}
