namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that stale audit successes are refreshed before the controller permits a merge.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class FreshCiTests(TestContext testContext)
{
    /// <summary>
    /// Does not try to rerun old successful statuses that are not GitHub Actions workflows.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ExternalStatusesDoNotTriggerWorkflowRefresh()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-fresh-ci-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var pr = TestData.PullRequest() with
            {
                Checks = [new CheckSnapshot("external", "SUCCESS", "https://checks.example.test/1", DateTimeOffset.UtcNow.AddDays(-7))]
            };
            services.PullRequests.Add(pr);
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.AreEqual(0, services.CheckRefreshes);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Refreshes old green checks, then verifies the newly merged revision separately.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task OldGreenChecksAreRefreshedBeforeMerge()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-fresh-ci-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var pr = TestData.PullRequest();
            services.PullRequests.Add(pr with
            {
                Checks = pr.Checks.Select(check => check with { CompletedAt = DateTimeOffset.UtcNow.AddDays(-7) }).ToArray()
            });
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var preview = await runner.RunAsync(TestData.Settings(true), testContext.CancellationToken);
            Assert.AreEqual("would-refresh", preview.Single().Outcome);
            Assert.AreEqual(0, services.CheckRefreshes);
            var actual = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("merged", actual.Single().Outcome);
            Assert.AreEqual(1, services.CheckRefreshes);
            Assert.AreEqual(1, services.Merges);
            Assert.Contains(new string('c', 40), services.VerifiedCommits);
        }
        finally { Directory.Delete(directory, true); }
    }
}
