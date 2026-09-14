namespace Depkeeper.Cli.Tests;

/// <summary>
/// Exercises maintenance decisions without model requests or GitHub writes.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class MaintenanceRunnerTests(TestContext testContext)
{
    /// <summary>
    /// Retains the lineage attempt budget across pushed revisions and subsequent sweeps.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task FailedCiCannotResetTheRepairBudget()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest() with { Checks = [new CheckSnapshot("tests", "FAILURE", "")] });
            services.OnRepair = pr =>
            {
                services.PullRequests[0] = pr with { Head = new string((char)('a' + services.Repairs), 40) };
                return new RepairResult(services.PullRequests[0].Head, "Candidate repair.");
            };
            var path = Path.Combine(directory, "state.json");
            var settings = TestData.Settings() with { MaxRepairs = 1 };
            for (var sweep = 0; sweep < 3; sweep++)
            {
                var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(),
                    TestData.AgeGate(), pollInterval: TimeSpan.Zero);
                await runner.RunAsync(settings, testContext.CancellationToken);
            }
            Assert.AreEqual(2, services.Repairs);
            Assert.AreEqual(0, services.Merges);
            Assert.IsTrue(new StateStore(path).State.PullRequests.Values.Single().Blocked);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Reevaluates publication metadata on the repaired head before allowing a merge.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task FreshDependencyInRepairMustCompleteCooldown()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var original = TestData.PullRequest();
            services.PullRequests.Add(original with { Checks = [new CheckSnapshot("tests", "FAILURE", "")] });
            services.OnRepair = _ =>
            {
                services.PullRequests[0] = original with { Head = new string('b', 40) };
                return new RepairResult(services.PullRequests[0].Head, "Updated dependency.");
            };
            var age = new ReleaseAgeGate((pr, _) => Task.FromResult<IReadOnlyList<DependencyChange>>(
                [new("added", "npm", "example", pr.Head == original.Head ? "old" : "new", [])]),
                (change, _) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddDays(change.Version == "old" ? -7 : 0)));
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Combine(directory, "state.json")),
                new Redactor(), age, pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings() with { ReleaseAge = new ReleaseAgePolicy() },
                testContext.CancellationToken);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(0, services.Merges);
            Assert.AreEqual("cooldown", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Merges a repaired commit only after a fresh successful GitHub check snapshot.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RepairedHeadMustPassFreshCiBeforeMerge()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest() with
            {
                MergeState = "BLOCKED",
                Checks = [new CheckSnapshot("tests", "FAILURE", "")]
            });
            services.OnRepair = pr =>
            {
                services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
                return new RepairResult(services.PullRequests[0].Head, "Fixed the incompatibility.");
            };
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Combine(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(1, services.Merges);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.IsGreaterThanOrEqualTo(3, services.Refreshes);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Keeps inspection mode free of repair sessions and GitHub mutations.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task DryRunNeverRepairsOrMerges()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest());
            var state = new StateStore(Path.Combine(directory, "state.json"));
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(true), testContext.CancellationToken);
            Assert.AreEqual("would-merge", results.Single().Outcome);
            Assert.AreEqual(0, services.Merges);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual(0, services.Comments);
            Assert.IsFalse(File.Exists(Path.Combine(directory, "state.json")));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Rechecks the PR head immediately before permitting a merge.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ChangedHeadIsNeverMerged()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices
            {
                OnRefresh = (pr, count) => count > 1 ? pr with { Head = new string('b', 40) } : pr
            };
            services.PullRequests.Add(TestData.PullRequest());
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Combine(directory, "state.json")),
                new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(0, services.Merges);
            Assert.AreEqual("blocked", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Persists failures and avoids repeating an unchanged blocked repair.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task FailedRepairIsCheckpointedAndNotRepeated()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest() with
            {
                MergeState = "BLOCKED",
                Checks = [new CheckSnapshot("tests", "FAILURE", "")]
            });
            var path = Path.Combine(directory, "state.json");
            var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(1, services.Comments);
            Assert.AreEqual(0, services.Merges);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Stops further mutations in a repository after merging one independently verified PR.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task MergesOneVerifiedPrPerRepositoryPerSweep()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.AddRange([TestData.PullRequest(), TestData.PullRequest(2)]);
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Combine(directory, "state.json")),
                new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.Merges);
            Assert.AreEqual(0, services.Repairs);
            Assert.ContainsSingle(entry => entry.Outcome == "merged", results);
            Assert.ContainsSingle(entry => entry.Outcome == "deferred", results);
        }
        finally { Directory.Delete(directory, true); }
    }
}
