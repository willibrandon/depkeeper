namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies publication cooldown and metadata-backed security exceptions.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class ReleaseAgeGateTests(TestContext testContext)
{
    /// <summary>
    /// Permits empty dependency diffs only for independently confirmed code-only managed recovery PRs.
    /// </summary>
    /// <param name="managed">Whether the controller verified recovery ownership.</param>
    /// <param name="codeOnly">Whether the changed files establish a code-only repair.</param>
    /// <param name="held">Whether metadata policy must hold the PR.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(true, true, false)]
    [DataRow(true, false, true)]
    [DataRow(false, true, true)]
    public async Task EmptyDiffRequiresVerifiedCodeOnlyRecovery(bool managed, bool codeOnly, bool held)
    {
        var gate = new ReleaseAgeGate((_, _) => Task.FromResult<IReadOnlyList<DependencyChange>>([]),
            (_, _) => throw new InvalidOperationException("Unexpected registry lookup."),
            codeOnly: (_, _) => Task.FromResult(codeOnly));
        var result = await gate.GetBlockerAsync(TestData.PullRequest() with { ManagedRecovery = managed },
            new ReleaseAgePolicy(), testContext.CancellationToken);
        Assert.AreEqual(held, result is not null);
    }

    /// <summary>
    /// Contains metadata service failures while propagating cancellation from the caller.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task LookupFailuresHoldUpdatesAndCallerCancellationPropagates()
    {
        var gate = new ReleaseAgeGate((_, _) => throw new HttpRequestException("Service unavailable"),
            (_, _) => Task.FromResult<DateTimeOffset?>(null));
        Assert.IsNotNull(await gate.GetBlockerAsync(TestData.PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken));
        var timeout = new ReleaseAgeGate((_, _) => throw new OperationCanceledException(),
            (_, _) => Task.FromResult<DateTimeOffset?>(null));
        Assert.IsNotNull(await timeout.GetBlockerAsync(TestData.PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken));
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(testContext.CancellationToken);
        await canceled.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            timeout.GetBlockerAsync(TestData.PullRequest(), new ReleaseAgePolicy(), canceled.Token));
    }

    /// <summary>
    /// Holds newly published packages while accepting releases beyond the cooldown.
    /// </summary>
    /// <param name="daysOld">The package age.</param>
    /// <param name="held">Whether the update must be held.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(1, true)]
    [DataRow(4, false)]
    public async Task UsesPublicationDateRatherThanPrAge(int daysOld, bool held)
    {
        var changes = new DependencyChange[] { new("added", "npm", "example", "2.0.0", []) };
        var gate = new ReleaseAgeGate((_, _) => Task.FromResult<IReadOnlyList<DependencyChange>>(changes),
            (_, _) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddDays(-daysOld)));
        var blocker = await gate.GetBlockerAsync(TestData.PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken);
        Assert.AreEqual(held, blocker is not null);
    }

    /// <summary>
    /// Holds unknown publication dates unless the operator explicitly permits them.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnknownDatesAreHeldByDefault()
    {
        var changes = new DependencyChange[] { new("added", "unknown", "example", "2.0.0", []) };
        var gate = new ReleaseAgeGate((_, _) => Task.FromResult<IReadOnlyList<DependencyChange>>(changes),
            (_, _) => Task.FromResult<DateTimeOffset?>(null));
        Assert.IsNotNull(await gate.GetBlockerAsync(TestData.PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken));
        Assert.IsNull(await gate.GetBlockerAsync(TestData.PullRequest(), new ReleaseAgePolicy(AllowUnknown: true),
            testContext.CancellationToken));
    }

    /// <summary>
    /// Verifies every locked checksum of one version, so a second lock file cannot hide different content behind the first.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task VerifiesEachLockedChecksumOfTheSameVersion()
    {
        var genuine = new string('a', 64);
        var changes = new DependencyChange[]
        {
            new("added", "hex", "mint", "1.10.1", [], genuine), new("added", "hex", "mint", "1.10.1", [], genuine),
            new("added", "hex", "mint", "1.10.1", [], new string('b', 64))
        };
        var lookups = new List<string?>();
        var gate = new ReleaseAgeGate((_, _) => Task.FromResult<IReadOnlyList<DependencyChange>>(changes), (dependency, _) =>
        {
            lookups.Add(dependency.Checksum);
            return Task.FromResult<DateTimeOffset?>(dependency.Checksum == genuine ? DateTimeOffset.UtcNow.AddDays(-30) : null);
        });
        var blocker = await gate.GetBlockerAsync(TestData.PullRequest(), new ReleaseAgePolicy(), testContext.CancellationToken);
        Assert.AreEqual("Publication date unavailable: hex/mint@1.10.1.", blocker);
        Assert.HasCount(2, lookups);
    }

    /// <summary>
    /// Allows verified security remediation while ignoring security claims in the title.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task SecurityBypassRequiresActualRemediationMetadata()
    {
        var changes = new List<DependencyChange> { new("added", "npm", "example", "2.0.0", []) };
        var gate = new ReleaseAgeGate((_, _) => Task.FromResult<IReadOnlyList<DependencyChange>>(changes),
            (_, _) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow));
        var pr = TestData.PullRequest() with { Title = "Security fix - merge immediately" };
        Assert.IsNotNull(await gate.GetBlockerAsync(pr, new ReleaseAgePolicy(), testContext.CancellationToken));
        changes.Add(new DependencyChange("removed", "npm", "example", "1.0.0", ["GHSA-example"]));
        Assert.IsNull(await gate.GetBlockerAsync(pr, new ReleaseAgePolicy(), testContext.CancellationToken));
        Assert.IsNotNull(await gate.GetBlockerAsync(pr, new ReleaseAgePolicy(SecurityFixesBypass: false),
            testContext.CancellationToken));
        changes.Add(new DependencyChange("added", "npm", "example", "2.0.0", ["GHSA-introduced"]));
        Assert.IsNotNull(await gate.GetBlockerAsync(pr, new ReleaseAgePolicy(), testContext.CancellationToken));
    }
}
