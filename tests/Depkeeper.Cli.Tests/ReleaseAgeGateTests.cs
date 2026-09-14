namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies publication cooldown and metadata-backed security exceptions.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class ReleaseAgeGateTests(TestContext testContext)
{
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
