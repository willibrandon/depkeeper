using System.Text;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies mix.lock fallback metadata against the exact PR base and head, through to the publication-age decision.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class MixGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Adds Hex changes from the exact revisions when GitHub dependency review returns no records.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task EmptyDependencyReviewFallsBackToExactLockFiles()
    {
        var requests = new List<string>();
        var gateway = Gateway("mix.lock", "modified", MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.0", 'a')),
            MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'b')), requests);
        var changes = await gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Ecosystem == "hex" && change.Name == "mint" &&
            change.Version == "1.10.1" && change.Checksum == MixLockFixture.Checksum('b'), changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Version == "1.10.0", changes);
        Assert.ContainsSingle(endpoint => endpoint.Contains("ref=base-head", StringComparison.Ordinal), requests);
        Assert.ContainsSingle(endpoint => endpoint.Contains("ref=" + PullRequest().Head, StringComparison.Ordinal), requests);
    }

    /// <summary>
    /// Reads nested lock files by their full path and lists the PR's files only once for every fallback parser.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ReadsNestedLockFilesAndListsFilesOnce()
    {
        var requests = new List<string>();
        var gateway = Gateway("apps/web/mix.lock", "modified", MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.0", 'a')),
            MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'b')), requests);
        var changes = await gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken);
        Assert.HasCount(2, changes);
        Assert.HasCount(2, requests.Where(endpoint => endpoint.Contains("contents/apps%2Fweb%2Fmix.lock", StringComparison.Ordinal))
            .ToArray());
        Assert.ContainsSingle(endpoint => endpoint.Contains("/pulls/1/files", StringComparison.Ordinal), requests);
    }

    /// <summary>
    /// Holds the update when the changed lock cannot be read at the head, instead of reporting only removals.
    /// </summary>
    /// <param name="status">GitHub's file status.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("modified")]
    [DataRow("added")]
    [DataRow("renamed")]
    [DataRow("")]
    public async Task UnreadableHeadLockIsNotTreatedAsRemoval(string status)
    {
        var gateway = Gateway("mix.lock", status, MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.0", 'a')), null, []);
        await Assert.ThrowsAsync<IOException>(() => gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken));
    }

    /// <summary>
    /// Accepts a genuinely deleted lock file as removals that need no publication metadata.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task DeletedLockFileReportsOnlyRemovals()
    {
        var gateway = Gateway("mix.lock", "removed", MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.0", 'a')), null, []);
        var changes = await gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Name == "mint", changes);
        Assert.HasCount(1, changes);
    }

    /// <summary>
    /// Leaves PRs that do not touch a lock file without Hex records or extra content requests.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task IgnoresPullRequestsWithoutLockChanges()
    {
        var requests = new List<string>();
        var gateway = Gateway("lib/postern.ex", "modified", null, null, requests);
        Assert.IsEmpty(await gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken));
        Assert.DoesNotContain(endpoint => endpoint.Contains("/contents/", StringComparison.Ordinal), requests);
    }

    /// <summary>
    /// Carries a lock update through the real gate: fresh releases cool down, mature ones pass, and unverifiable ones hold.
    /// </summary>
    /// <param name="publishedDaysAgo">The registry age of the locked release.</param>
    /// <param name="registryChecksum">The checksum character hex.pm reports.</param>
    /// <param name="expected">The expected start of the hold reason, or null when the update may proceed.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(1, 'b', "Cooling down: mint@1.10.1")]
    [DataRow(4, 'b', null)]
    [DataRow(400, 'c', "Publication date unavailable: hex/mint@1.10.1")]
    public async Task LockUpdatesReachThePublicationAgeDecision(int publishedDaysAgo, char registryChecksum, string? expected)
    {
        var gateway = Gateway("mix.lock", "modified", MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.0", 'a')),
            MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'b')), []);
        var inserted = DateTimeOffset.UtcNow.AddDays(-publishedDaysAgo).ToString("O");
        using var http = new HttpClient(new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(
            "{\"version\":\"1.10.1\",\"checksum\":\"" + MixLockFixture.Checksum(registryChecksum) +
            "\",\"retirement\":null,\"inserted_at\":\"" + inserted + "\"}")));
        var blocker = await Gate(gateway, http).GetBlockerAsync(PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken);
        if (expected is null) Assert.IsNull(blocker);
        else Assert.StartsWith(expected, blocker);
    }

    /// <summary>
    /// Holds a lock that mixes a verifiable Hex update with a Git revision that has no publication metadata.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task GitRevisionChangesHoldAnOtherwiseMatureUpdate()
    {
        const string url = "https://github.com/example/library.git";
        var gateway = Gateway("mix.lock", "modified",
            MixLockFixture.Lock(MixLockFixture.Git("library", url, 'a'), MixLockFixture.Hex("mint", "1.10.0", 'a')),
            MixLockFixture.Lock(MixLockFixture.Git("library", url, 'b'), MixLockFixture.Hex("mint", "1.10.1", 'b')), []);
        using var http = new HttpClient(new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(
            "{\"version\":\"1.10.1\",\"checksum\":\"" + MixLockFixture.Checksum('b') +
            "\",\"retirement\":null,\"inserted_at\":\"" + DateTimeOffset.UtcNow.AddDays(-30).ToString("O") + "\"}")));
        var blocker = await Gate(gateway, http).GetBlockerAsync(PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken);
        Assert.StartsWith("Publication date unavailable: git/" + url, blocker);
    }

    /// <summary>
    /// Holds a conflicted or truncated head lock through the gate's metadata-failure path without contacting hex.pm.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnreadableLockContentHoldsTheUpdate()
    {
        var gateway = Gateway("mix.lock", "modified", MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.0", 'a')),
            "%{\n<<<<<<< HEAD\n" + MixLockFixture.Hex("mint", "1.10.1", 'b') + "=======\n>>>>>>> pr\n}\n", []);
        var handler = new TestHttpMessageHandler(_ => throw new InvalidOperationException("Unexpected registry request."));
        using var http = new HttpClient(handler);
        var blocker = await Gate(gateway, http).GetBlockerAsync(PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken);
        Assert.AreEqual("Dependency publication lookup failed; automatic merging is held until metadata is available.", blocker);
        Assert.AreEqual(0, handler.Requests);
    }

    private static ReleaseAgeGate Gate(GitHubGateway gateway, HttpClient http)
    {
        var publications = new PublicationClient(http, hex: new HexPublicationClient(http).GetAsync);
        return new ReleaseAgeGate(gateway.GetDependencyChangesAsync, publications.GetAsync);
    }

    private static PullRequestSnapshot PullRequest() => TestData.PullRequest() with { BaseHead = "base-head" };

    private static GitHubGateway Gateway(string path, string status, string? before, string? after, List<string> requests) =>
        new("", new Redactor(), (arguments, _, _) =>
        {
            var endpoint = arguments[1];
            requests.Add(endpoint);
            if (endpoint.Contains("dependency-graph", StringComparison.Ordinal)) return Result("[]");
            if (endpoint.Contains("/pulls/1/files", StringComparison.Ordinal))
                return Result("[[{\"filename\":\"" + path + "\",\"status\":\"" + status + "\"}]]");
            var content = endpoint.Contains("ref=base-head", StringComparison.Ordinal) ? before : after;
            return Task.FromResult(content is null ? new CommandResult(1, "", "Not Found") : new CommandResult(0,
                "{\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(content)) + "\"}", ""));
        });

    private static Task<CommandResult> Result(string output) => Task.FromResult(new CommandResult(0, output, ""));
}
