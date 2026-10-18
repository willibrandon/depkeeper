namespace Depkeeper.Cli.Tests;

/// <summary>
/// Exercises bounded local-validation feedback, durable attempt budgets, and fresh blocked reports.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class RepairFeedbackTests(TestContext testContext)
{
    /// <summary>
    /// Resumes the unused validation attempt in an older checkpoint without resetting its consumed budget.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task LegacyValidationBlockCanUseItsRemainingAttempt()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-feedback-").FullName;
        try
        {
            var (services, state) = Create(directory);
            var pr = services.PullRequests[0];
            const string feedback = "Independent validation failed: audit source-map-js.";
            state.Set(pr.Key, new AttemptState(pr.Head, 1, true,
                "Checks still fail after the branch was brought up to date; the earlier blocker stands: " + feedback,
                DateTimeOffset.UtcNow));
            services.OnRepair = current =>
            {
                Assert.AreEqual(2, state.State.PullRequests[pr.Key].Attempts);
                Assert.IsNotNull(services.LastRepairEvidence);
                Assert.Contains(feedback, services.LastRepairEvidence.Context);
                throw new RepairValidationException("Independent validation failed: still incompatible.");
            };
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());

            var result = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            var repeated = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("blocked", result.Single().Outcome);
            Assert.AreEqual("blocked", repeated.Single().Outcome);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(2, state.State.PullRequests[pr.Key].Attempts);
            Assert.AreEqual(0, services.Merges);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Gives the next bounded repair current CI and the independently discovered candidate failure separately.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ValidationFeedbackCanProduceAVerifiedMerge()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-feedback-").FullName;
        try
        {
            var (services, state) = Create(directory);
            services.OnRepair = pr =>
            {
                if (services.Repairs == 1) throw new RepairValidationException("Independent validation failed: npm audit source-map-js.");
                Assert.IsNotNull(services.LastRepairEvidence);
                Assert.Contains("npm audit source-map-js", services.LastRepairEvidence.Context);
                Assert.DoesNotContain("source-map-js", services.LastRepairEvidence.CurrentFailures);
                Assert.AreEqual(2, state.State.PullRequests[pr.Key].Attempts);
                services.PullRequests[0] = pr with { Head = new string('b', 40), Checks = TestData.PullRequest().Checks };
                return new RepairResult(services.PullRequests[0].Head, "Validated repair.");
            };
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);

            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.AreEqual(2, services.Repairs);
            Assert.AreEqual(1, services.Merges);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Stops exhausted local repairs and preserves a redacted explanation across later sweeps.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ExhaustedValidationFailuresAreNotRepeatedDaily()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-feedback-").FullName;
        try
        {
            var (services, state) = Create(directory);
            services.OnRepair = _ => throw new RepairValidationException("Independent validation failed: runtime-secret audit.");
            var redactor = new Redactor("runtime-secret");
            var runner = new MaintenanceRunner(services, services, state, redactor, TestData.AgeGate());

            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            state = new StateStore(Path.Join(directory, "state.json"));
            runner = new MaintenanceRunner(services, services, state, redactor, TestData.AgeGate());
            var repeated = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.AreEqual("blocked", repeated.Single().Outcome);
            Assert.AreEqual(2, services.Repairs);
            Assert.AreEqual(1, services.Comments);
            Assert.AreEqual(0, services.Merges);
            Assert.AreEqual(2, state.State.PullRequests.Values.Single().Attempts);
            Assert.IsTrue(state.State.PullRequests.Values.Single().Blocked);
            Assert.DoesNotContain("runtime-secret", File.ReadAllText(Path.Join(directory, "state.json")));
            Assert.Contains("REDACTED", repeated.Single().Detail);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Carries validation feedback into a later sweep when the daily repair budget ends first.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task DailyBudgetDefersWithDurableValidationFeedback()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-feedback-").FullName;
        try
        {
            var (services, state) = Create(directory);
            services.OnRepair = _ => throw new RepairValidationException("Independent validation failed: newly discovered audit.");
            var settings = TestData.Settings() with { MaxRepairs = 1 };
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());

            var first = await runner.RunAsync(settings, testContext.CancellationToken);
            state = new StateStore(Path.Join(directory, "state.json"));
            Assert.AreEqual("deferred", first.Single().Outcome);
            Assert.IsFalse(state.State.PullRequests.Values.Single().Blocked);
            services.OnRepair = pr =>
            {
                Assert.IsNotNull(services.LastRepairEvidence);
                Assert.Contains("newly discovered audit", services.LastRepairEvidence.Context);
                services.PullRequests[0] = pr with { Head = new string('b', 40), Checks = TestData.PullRequest().Checks };
                return new RepairResult(services.PullRequests[0].Head, "Validated repair.");
            };
            runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var second = await runner.RunAsync(settings, testContext.CancellationToken);

            Assert.AreEqual("merged", second.Single().Outcome);
            Assert.AreEqual(2, services.Repairs);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Keeps security-policy and infrastructure exceptions terminal instead of retrying them as candidate failures.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task SafetyFailuresRemainTerminal()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-feedback-").FullName;
        try
        {
            var (services, state) = Create(directory);
            services.OnRepair = _ => throw new InvalidOperationException("Repair changed a protected path.");
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());

            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(0, services.Merges);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Rebuilds current CI details on every blocked sweep without recursively embedding earlier reports.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task HistoricalFailureIsClearlySeparatedFromCurrentChecks()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-feedback-").FullName;
        try
        {
            var (services, state) = Create(directory);
            var pr = services.PullRequests[0];
            state.Set(pr.Key, new AttemptState(pr.Head, 2, true, "Independent validation failed: old source-map-js advisory.",
                DateTimeOffset.UtcNow));
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var first = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            var repeated = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.Contains("Current CI at " + pr.Head + ": policy tests (FAILURE)", first.Single().Detail);
            Assert.Contains("Previous repair result: Independent validation failed: old source-map-js advisory.", first.Single().Detail);
            Assert.AreEqual(first.Single().Detail, repeated.Single().Detail);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual(0, services.Comments);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (FakeMaintenanceServices Services, StateStore State) Create(string directory)
    {
        var services = new FakeMaintenanceServices();
        services.PullRequests.Add(TestData.PullRequest() with { Checks = [new CheckSnapshot("policy tests", "FAILURE", "")] });
        return (services, new StateStore(Path.Join(directory, "state.json")));
    }
}
