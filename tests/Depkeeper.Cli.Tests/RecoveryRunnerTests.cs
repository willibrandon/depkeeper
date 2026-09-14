namespace Depkeeper.Cli.Tests;

/// <summary>
/// Exercises bounded follow-up PR creation, resumption, and exact-commit verification after a failed merge.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class RecoveryRunnerTests(TestContext testContext)
{
    /// <summary>
    /// Can enroll an explicitly selected historical merge whose PR is no longer open.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ExplicitMergedPrCanEnterRecoveryAfterDeployment()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-recovery-").FullName;
        try
        {
            var services = new FakeMaintenanceServices
            {
                OnCommitChecks = commit => [new CheckSnapshot("build", commit[0] == 'c' ? "FAILURE" : "SUCCESS", "")]
            };
            var source = TestData.PullRequest() with { State = "MERGED", MergeCommit = new string('c', 40) };
            services.PullRequests.Add(source);
            var settings = TestData.Settings() with
            {
                OnlyPullRequest = source.Number,
                Profiles = new Dictionary<string, RepositoryProfile> { [source.Repository] = new(AutoRecover: true) },
                CiTimeout = TimeSpan.Zero,
                MaxRepairs = 1
            };
            var state = new StateStore(Path.Join(directory, "state.json"));
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(settings, testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.AreEqual(1, services.CreatedRecoveries);
            Assert.AreEqual(1, services.Repairs);
            Assert.IsEmpty(state.State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Resolves transient failures by rerunning CI before spending model credits or creating a PR.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task TransientPostMergeFailureDoesNotCreateARepairPr()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-recovery-").FullName;
        try
        {
            var (services, state, settings) = Create(directory);
            services.FailedRerunSucceeds = true;
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(settings, testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.AreEqual(1, services.CheckRefreshes);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual(0, services.CreatedRecoveries);
            Assert.IsEmpty(state.State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Creates one recovery PR and verifies its merged commit before clearing the original blocker.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task PersistentPostMergeFailureGetsAVerifiedRecoveryPr()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-recovery-").FullName;
        try
        {
            var (services, state, settings) = Create(directory);
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(),
                pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(settings, testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.Contains("Recovery PR #1001", results.Single().Detail);
            Assert.AreEqual(1, services.CreatedRecoveries);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(1, services.Merges);
            Assert.Contains(new string('e', 40), services.VerifiedCommits);
            Assert.IsEmpty(state.State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Resumes the same recovery PR after CI finishes without creating another branch or repair session.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task PendingRecoveryResumesWithoutDuplicates()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-recovery-").FullName;
        try
        {
            var (services, state, settings) = Create(directory);
            services.RecoveryChecks = [new CheckSnapshot("tests", "IN_PROGRESS", "")];
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var first = await runner.RunAsync(settings, testContext.CancellationToken);
            Assert.AreEqual("pending", first.Single().Outcome);
            Assert.AreEqual(1001, state.State.PullRequests.Values.Single().Recovery!.PullRequest);
            services.PullRequests[1] = services.PullRequests[1] with { Checks = TestData.PullRequest().Checks };
            state = new StateStore(Path.Join(directory, "state.json"));
            runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var second = await runner.RunAsync(settings with { MaxRepairs = 0 }, testContext.CancellationToken);
            Assert.AreEqual("merged", second.Single().Outcome);
            Assert.AreEqual(1, services.CreatedRecoveries);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(1, services.Merges);
            Assert.IsEmpty(state.State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Remembers failed recovery work and respects a depleted sweep budget.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task FailedRecoveryIsNotRepeatedDaily()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-recovery-").FullName;
        try
        {
            var (services, state, settings) = Create(directory);
            services.OnRecovery = _ => new RepairResult(null, "No compatible repair was found.");
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var deferred = await runner.RunAsync(settings with { MaxRepairs = 0 }, testContext.CancellationToken);
            Assert.AreEqual("deferred", deferred.Single().Outcome);
            Assert.AreEqual(0, services.Repairs);
            await runner.RunAsync(settings, testContext.CancellationToken);
            runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate());
            var repeated = await runner.RunAsync(settings, testContext.CancellationToken);
            Assert.AreEqual("blocked", repeated.Single().Outcome);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(0, services.CreatedRecoveries);
            Assert.AreEqual(0, services.Merges);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (FakeMaintenanceServices Services, StateStore State, RunSettings Settings) Create(string directory)
    {
        var services = new FakeMaintenanceServices
        {
            OnCommitChecks = commit => [new CheckSnapshot("build", commit[0] == 'c' ? "FAILURE" : "SUCCESS", "")]
        };
        var source = TestData.PullRequest() with { State = "MERGED", MergeCommit = new string('c', 40) };
        services.PullRequests.Add(source);
        var state = new StateStore(Path.Join(directory, "state.json"));
        state.Set(source.Key, new AttemptState(source.Head, 0, true, "Post-merge build failed.", DateTimeOffset.UtcNow,
            source.MergeCommit, "main"));
        var settings = TestData.Settings() with
        {
            Profiles = new Dictionary<string, RepositoryProfile> { [source.Repository] = new(AutoRecover: true) },
            MaxRepairs = 1,
            CiTimeout = TimeSpan.Zero
        };
        return (services, state, settings);
    }
}
