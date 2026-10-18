using System.Net;
using System.Text.Json.Nodes;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies upstream blocker classification without trusting CI text or bypassing npm peer resolution.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class NpmCompatibilityGateTests(TestContext testContext)
{
    /// <summary>
    /// Blocks only when no supported stable upgrade can satisfy the PR's peer version.
    /// </summary>
    /// <param name="release">An additional published version.</param>
    /// <param name="range">Its peer requirement.</param>
    /// <param name="deprecated">Whether the published release is deprecated.</param>
    /// <param name="compatible">The expected available upgrade.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("0.9.11", "^5.0.0 || ^6.0.0", false, null)]
    [DataRow("0.9.11", "^6.0.0 || ^7.0.0", false, "0.9.11")]
    [DataRow("0.10.0-beta.1", "^7.0.0", false, null)]
    [DataRow("0.9.11", "^7.0.0", true, null)]
    [DataRow("0.9.9", "*", false, null)]
    public async Task RequiresAStableNondeprecatedUpgrade(string release, string range, bool deprecated, string? compatible)
    {
        using var handler = new TestHttpMessageHandler(request =>
        {
            Assert.AreEqual("registry.npmjs.org", request.RequestUri!.Host);
            Assert.AreEqual("/@astrojs/check", Uri.UnescapeDataString(request.RequestUri.AbsolutePath));
            Assert.IsNull(request.Headers.Authorization);
            return TestHttpMessageHandler.Json(NpmCompatibilityFixture.Registry((release, range, deprecated)));
        });
        using var client = new HttpClient(handler);
        var result = await NpmCompatibilityFixture.Gate(client).CheckAsync(NpmCompatibilityFixture.PullRequest(),
            NpmCompatibilityFixture.Logs, testContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.AreEqual(compatible, result.CompatibleVersion);
        Assert.AreEqual("docs-site/package.json", result.Conflict.Manifest);
        Assert.AreEqual("7.0.2", result.Conflict.PeerVersion);
    }

    /// <summary>
    /// Refuses to infer compatibility from stale, misleading, ranged, or malformed declarations.
    /// </summary>
    /// <param name="manifest">The exact-head manifest returned by the controller.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("""{"devDependencies":{"@astrojs/check":"0.9.10","typescript":"6.0.3"}}""")]
    [DataRow("""{"devDependencies":{"@astrojs/check":"^0.9.10","typescript":"7.0.2"}}""")]
    [DataRow("""{"devDependencies":{"typescript":"7.0.2"}}""")]
    [DataRow("[]")]
    [DataRow("invalid json")]
    public async Task LogsAloneCannotEstablishAnUpstreamBlocker(string manifest)
    {
        using var handler = new TestHttpMessageHandler(_ => throw new AssertFailedException("Unexpected registry request."));
        using var client = new HttpClient(handler);

        var result = await NpmCompatibilityFixture.Gate(client, manifest).CheckAsync(NpmCompatibilityFixture.PullRequest(),
            NpmCompatibilityFixture.Logs, testContext.CancellationToken);

        Assert.IsNull(result);
        Assert.AreEqual(0, handler.Requests);
    }

    /// <summary>
    /// Treats missing, malformed, or mismatched registry evidence as unknown rather than proof of incompatibility.
    /// </summary>
    /// <param name="status">The HTTP result.</param>
    /// <param name="metadata">The returned registry evidence.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(HttpStatusCode.NotFound, "{}")]
    [DataRow(HttpStatusCode.ServiceUnavailable, "{}")]
    [DataRow(HttpStatusCode.OK, "invalid json")]
    [DataRow(HttpStatusCode.OK, "[]")]
    [DataRow(HttpStatusCode.OK, """{"name":"other-package","versions":{}}""")]
    public async Task UnavailableRegistryEvidenceDoesNotClassifyAConflict(HttpStatusCode status, string metadata)
    {
        using var handler = new TestHttpMessageHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(metadata) });
        using var client = new HttpClient(handler);

        var result = await NpmCompatibilityFixture.Gate(client).CheckAsync(NpmCompatibilityFixture.PullRequest(),
            NpmCompatibilityFixture.Logs, testContext.CancellationToken);

        Assert.IsNull(result);
    }

    /// <summary>
    /// Does not spend registry requests on unrelated check failures or historical ERESOLVE context.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnrelatedFailuresDoNotTriggerRegistryAccess()
    {
        using var handler = new TestHttpMessageHandler(_ => throw new AssertFailedException("Unexpected registry request."));
        using var client = new HttpClient(handler);
        var gate = new NpmCompatibilityGate(client, (_, _) => throw new AssertFailedException("Unexpected manifest request."));

        Assert.IsNull(await gate.CheckAsync(NpmCompatibilityFixture.PullRequest(), "AssertionError: stale action pin",
            testContext.CancellationToken));
        Assert.AreEqual(0, handler.Requests);
    }

    /// <summary>
    /// Does not propose upgrading a requiring package whose exact value was introduced by Dependabot.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RequiringPackageUpdateCannotBeReplacedByACompanionUpgrade()
    {
        using var handler = new TestHttpMessageHandler(_ => throw new AssertFailedException("Unexpected registry request."));
        using var client = new HttpClient(handler);
        var pr = NpmCompatibilityFixture.PullRequest();
        var inspected = new List<string>();
        var gate = new NpmCompatibilityGate(client, (current, _) =>
        {
            inspected.Add(current.Head);
            var manifest = current.Head == pr.Head ? NpmCompatibilityFixture.Manifest :
                NpmCompatibilityFixture.Manifest.Replace("0.9.10", "0.9.9", StringComparison.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
            {
                ["docs-site/package.json"] = manifest
            });
        });

        var result = await gate.CheckAsync(pr, NpmCompatibilityFixture.Logs, testContext.CancellationToken);

        Assert.IsNull(result);
        Assert.AreEqual(0, handler.Requests);
        CollectionAssert.AreEqual(new[] { pr.Head, pr.BaseHead }, inspected);
    }

    /// <summary>
    /// Treats transient GitHub evidence failures as unknown without permitting a registry-based budget reset.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnavailableManifestEvidenceDoesNotClassifyAConflict()
    {
        using var handler = new TestHttpMessageHandler(_ => throw new AssertFailedException("Unexpected registry request."));
        using var client = new HttpClient(handler);
        var gate = new NpmCompatibilityGate(client, (_, _) => throw new IOException("GitHub unavailable."));

        var result = await gate.CheckAsync(NpmCompatibilityFixture.PullRequest(),
            NpmCompatibilityFixture.Logs, testContext.CancellationToken);

        Assert.IsNull(result);
    }

    /// <summary>
    /// Does not claim that every stable release is incompatible when one candidate's metadata is incomplete.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task IncompleteStableReleaseMetadataIsUnknown()
    {
        var registry = JsonNode.Parse(NpmCompatibilityFixture.Registry(("0.9.11", "^7.0.0", false)))!;
        registry["versions"]!["0.9.11"]!.AsObject().Remove("name");
        using var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(registry.ToJsonString()));
        using var client = new HttpClient(handler);

        var result = await NpmCompatibilityFixture.Gate(client).CheckAsync(NpmCompatibilityFixture.PullRequest(),
            NpmCompatibilityFixture.Logs, testContext.CancellationToken);

        Assert.IsNull(result);
    }
}
