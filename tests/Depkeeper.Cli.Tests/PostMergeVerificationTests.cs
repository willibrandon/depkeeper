namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that completed merges remain tracked until their own commit passes CI.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class PostMergeVerificationTests(TestContext testContext)
{
    /// <summary>
    /// Holds completion when a required base-branch check is missing despite other successful checks.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RequiredPostMergeChecksMustBePresent()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest());
            var state = new StateStore(Path.Join(directory, "state.json"));
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var settings = TestData.Settings() with
            {
                CiTimeout = TimeSpan.Zero,
                Profiles = new Dictionary<string, RepositoryProfile>
                {
                    ["owner/repository"] = new(PostMergeChecks: ["publish"])
                }
            };
            var results = await runner.RunAsync(settings, testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.Contains("publish", results.Single().Detail);
            Assert.IsNotNull(state.State.PullRequests.Values.Single().MergeHead);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Recognizes a verified descendant fix without claiming that the original failing commit passed.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task FollowUpFixCanResolveAnEarlierPostMergeFailure()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices
            {
                OnCommitChecks = commit => [new CheckSnapshot("build", commit[0] == 'c' ? "FAILURE" : "SUCCESS", "")]
            };
            services.PullRequests.Add(TestData.PullRequest());
            var path = Path.Join(directory, "state.json");
            var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            await runner.RunAsync(TestData.Settings() with { CiTimeout = TimeSpan.Zero }, testContext.CancellationToken);
            services.RecoveryCommit = new string('d', 40);
            runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.Contains("verified descendant", results.Single().Detail);
            Assert.Contains(services.RecoveryCommit, services.VerifiedCommits);
            Assert.IsEmpty(new StateStore(path).State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Polls checks on the merge commit instead of reusing the successful PR checks.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task SuccessfulPrChecksDoNotReplacePostMergeChecks()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest());
            services.OnCommitChecks = _ => services.VerifiedCommits.Count == 1
                ? [new CheckSnapshot("build", "IN_PROGRESS", "")] : [new CheckSnapshot("build", "SUCCESS", "")];
            var store = new StateStore(Path.Join(directory, "state.json"));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(),
                pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.HasCount(2, services.VerifiedCommits);
            Assert.IsTrue(services.VerifiedCommits.All(commit => commit == new string('c', 40)));
            Assert.IsEmpty(store.State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Keeps post-merge failures across sweeps and prevents further merges onto the failing base.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task PostMergeFailureIsRetainedUntilTheExactCommitPasses()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices
            {
                OnCommitChecks = _ => [new CheckSnapshot("push-build", "FAILURE", "")]
            };
            services.PullRequests.AddRange([TestData.PullRequest(), TestData.PullRequest(2)]);
            var path = Path.Join(directory, "state.json");
            var settings = TestData.Settings() with { CiTimeout = TimeSpan.Zero };
            for (var sweep = 0; sweep < 2; sweep++)
            {
                var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
                var results = await runner.RunAsync(settings, testContext.CancellationToken);
                Assert.ContainsSingle(entry => entry.Outcome == "blocked" && entry.Number == 1, results);
                Assert.ContainsSingle(entry => entry.Outcome == "deferred" && entry.Number == 2, results);
            }
            Assert.AreEqual(1, services.Merges);
            Assert.AreEqual(new string('c', 40), new StateStore(path).State.PullRequests.Values.Single().MergeHead);
            services.OnCommitChecks = _ => [new CheckSnapshot("push-build", "SUCCESS", "")];
            var resumed = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            var verified = await resumed.RunAsync(settings with { OnlyPullRequest = 1 }, testContext.CancellationToken);
            Assert.AreEqual("merged", verified.Single().Outcome);
            Assert.AreEqual(1, services.Merges);
            Assert.IsEmpty(new StateStore(path).State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Retains pending post-merge CI without raising an actionable blocker or repeating the merge.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task PendingPostMergeChecksResumeForClosedPrs()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices
            {
                OnCommitChecks = _ => [new CheckSnapshot("build", "QUEUED", "")]
            };
            services.PullRequests.Add(TestData.PullRequest());
            var path = Path.Join(directory, "state.json");
            var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings() with { CiTimeout = TimeSpan.Zero }, testContext.CancellationToken);
            Assert.AreEqual("queued", results.Single().Outcome);
            Assert.Contains("Exact-commit verification queued", results.Single().Detail);
            Assert.DoesNotContain("has failed or is still running", results.Single().Detail);
            Assert.IsFalse(new StateStore(path).State.PullRequests.Values.Single().Blocked);
            services.OnCommitChecks = _ => [new CheckSnapshot("build", "SUCCESS", "")];
            runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            var resumed = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("merged", resumed.Single().Outcome);
            Assert.AreEqual(1, services.Merges);
            Assert.IsEmpty(new StateStore(path).State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }
}
