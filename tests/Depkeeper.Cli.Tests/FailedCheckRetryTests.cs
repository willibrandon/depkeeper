namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that a failed check is rerun once per head before it costs a repair or keeps a blocked verdict.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class FailedCheckRetryTests(TestContext testContext)
{
    private const string Url = "https://github.com/owner/repository/actions/runs/123/job/456";

    /// <summary>
    /// Merges a transient failure after its rerun passes and repairs only a failure that persists.
    /// </summary>
    /// <param name="rerun">The result reported by the rerun.</param>
    /// <param name="repairs">The expected repair count.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("SUCCESS", 0)]
    [DataRow("FAILURE", 1)]
    public async Task FailedChecksAreRerunBeforeRepair(string rerun, int repairs)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { PullRequestRerunState = rerun };
            services.PullRequests.Add(Failing());
            services.OnRepair = _ =>
            {
                services.PullRequests[0] = TestData.PullRequest() with { Head = new string('d', 40) };
                return new RepairResult(services.PullRequests[0].Head, "Repaired.");
            };
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.CheckRefreshes);
            Assert.AreEqual(repairs, services.Repairs);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Clears a terminal verdict whose failure passes on its first rerun.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task BlockedFailurePassingItsRerunMerges()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { PullRequestRerunState = "SUCCESS" };
            var store = Blocked(services, directory);
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.CheckRefreshes);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Keeps a blocked verdict after its rerun fails again and does not rerun the same head on later sweeps.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task BlockedFailureIsRerunOnlyOnce()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { PullRequestRerunState = "FAILURE" };
            var store = Blocked(services, directory);
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var first = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            var second = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.CheckRefreshes);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual("blocked", first.Single().Outcome);
            Assert.AreEqual("blocked", second.Single().Outcome);
            Assert.AreEqual(services.PullRequests[0].Head, store.State.PullRequests[services.PullRequests[0].Key].CiRetriedHead);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static PullRequestSnapshot Failing() => TestData.PullRequest() with
    {
        MergeState = "UNSTABLE",
        Checks = [new CheckSnapshot("tests", "FAILURE", Url, DateTimeOffset.UtcNow.AddMinutes(-5))]
    };

    private static StateStore Blocked(FakeMaintenanceServices services, string directory)
    {
        var pr = Failing();
        services.PullRequests.Add(pr);
        var store = new StateStore(Path.Join(directory, "state.json"));
        store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Blocked.", DateTimeOffset.UtcNow, BaseHead: services.BaseHead,
            RepairFailure: "Repair changed protected package manifest fields in package.json: engines."));
        return store;
    }
}
