namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that repairs target the current base and that a conflicting controller repair never strands its update.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class ConflictingRepairTests(TestContext testContext)
{
    /// <summary>
    /// Asks Dependabot to recreate a repaired branch that GitHub cannot merge with its base, then merges the passing update.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ConflictingRepairIsDiscardedByRecreatingTheUpdate()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = BranchOwnership.DependabotWithRepairs };
            services.PullRequests.Add(TestData.PullRequest() with { MergeState = "DIRTY", Mergeable = "CONFLICTING" });
            services.OnComment = (_, _) => services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.BranchUpdates);
            Assert.AreEqual("@dependabot recreate", services.CommentBodies.Single());
            Assert.AreEqual(1, services.Merges);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Keeps a verified repair by merging the base when GitHub can update the branch without conflicts.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RepairedBranchThatMergesCleanlyKeepsItsRepair()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = BranchOwnership.DependabotWithRepairs, AcceptUpdateBranch = true };
            services.PullRequests.Add(TestData.PullRequest() with { MergeState = "BEHIND" });
            services.OnRefresh = (current, _) => services.BranchUpdates > 0
                ? current with { Head = new string('b', 40), MergeState = "CLEAN" } : current;
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(1, services.BranchUpdates);
            Assert.IsEmpty(services.CommentBodies);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Reopens a revision blocked only because its passing repair conflicts with an unchanged base, keeping the consumed budget.
    /// </summary>
    /// <param name="consumed">The repair attempts already spent on the update.</param>
    /// <param name="repairs">The repairs expected on the recreated head.</param>
    /// <param name="outcome">The expected outcome.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(1, 1, "merged")]
    [DataRow(2, 0, "blocked")]
    public async Task BlockedConflictingRepairIsRecreatedWithinItsBudget(int consumed, int repairs, string outcome)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = BranchOwnership.DependabotWithRepairs };
            var pr = TestData.PullRequest() with { MergeState = "DIRTY", Mergeable = "CONFLICTING" };
            services.PullRequests.Add(pr);
            services.OnComment = (_, body) =>
            {
                if (body.StartsWith("@dependabot", StringComparison.Ordinal))
                    services.PullRequests[0] = TestData.PullRequest() with
                    {
                        Head = new string('b', 40),
                        Checks = [new CheckSnapshot("tests", "FAILURE", "")]
                    };
            };
            services.OnRepair = _ =>
            {
                services.PullRequests[0] = TestData.PullRequest() with { Head = new string('d', 40) };
                return new RepairResult(services.PullRequests[0].Head, "Repaired against the current base.");
            };
            var path = Path.Join(directory, "state.json");
            var store = new StateStore(path);
            store.Set(pr.Key, new AttemptState(pr.Head, consumed, true, "GitHub has not confirmed the branch is conflict-free.",
                DateTimeOffset.UtcNow, BaseHead: services.BaseHead,
                RepairFailure: "GitHub could not update the branch; resolve conflicts or permissions."));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("@dependabot recreate", services.CommentBodies[0]);
            Assert.AreEqual(repairs, services.Repairs);
            Assert.AreEqual(outcome, results.Single().Outcome);
            if (outcome == "blocked") Assert.AreEqual(2, new StateStore(path).State.PullRequests[pr.Key].Attempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Recreates a blocked repair that never received checks because GitHub skips pull request workflows during a conflict.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ConflictingRepairWithoutChecksIsRecreated()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = BranchOwnership.DependabotWithRepairs };
            var pr = TestData.PullRequest() with { MergeState = "DIRTY", Mergeable = "CONFLICTING", Checks = [] };
            services.PullRequests.Add(pr);
            services.OnComment = (_, _) => services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
            var store = new StateStore(Path.Join(directory, "state.json"));
            store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Blocked.", DateTimeOffset.UtcNow, BaseHead: services.BaseHead,
                RepairFailure: "GitHub could not update the branch; resolve conflicts or permissions."));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("@dependabot recreate", services.CommentBodies.Single());
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Leaves a blocked conflicting revision alone when its repair still fails or the branch carries foreign work.
    /// </summary>
    /// <param name="ownership">The branch ownership name.</param>
    /// <param name="state">The current check state.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("DependabotWithRepairs", "FAILURE")]
    [DataRow("Foreign", "SUCCESS")]
    public async Task OnlyPassingControllerRepairsAreReopened(string ownership, string state)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = Enum.Parse<BranchOwnership>(ownership) };
            var pr = TestData.PullRequest() with
            {
                MergeState = "DIRTY",
                Mergeable = "CONFLICTING",
                Checks = [new CheckSnapshot("tests", state, "")]
            };
            services.PullRequests.Add(pr);
            var store = new StateStore(Path.Join(directory, "state.json"));
            store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Blocked.", DateTimeOffset.UtcNow, BaseHead: services.BaseHead));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.AreEqual(0, services.Comments);
            Assert.AreEqual(0, services.Repairs);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Discards a repair that conflicts with the base after it was pushed, and spends only the remaining budget on the recreated head.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RepairThatConflictsAfterPushKeepsItsBudgetAcrossRecreation()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var original = TestData.PullRequest() with { Checks = [new CheckSnapshot("tests", "FAILURE", "")] };
            services.PullRequests.Add(original);
            services.OnRepair = _ =>
            {
                services.Ownership = BranchOwnership.DependabotWithRepairs;
                services.PullRequests[0] = services.Repairs == 1
                    ? TestData.PullRequest() with { Head = new string('m', 40), MergeState = "DIRTY", Mergeable = "CONFLICTING" }
                    : original with { Head = new string((char)('m' + services.Repairs), 40) };
                return new RepairResult(services.PullRequests[0].Head, "Repaired.");
            };
            var path = Path.Join(directory, "state.json");
            var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate(),
                pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("pending", results.Single().Outcome);
            Assert.AreEqual("@dependabot recreate", services.CommentBodies.Single());
            var saved = new StateStore(path).State.PullRequests[original.Key];
            Assert.IsTrue(saved.RebaseRequested);
            Assert.IsFalse(saved.Blocked);
            Assert.AreEqual(1, saved.Attempts);

            services.Ownership = BranchOwnership.Dependabot;
            services.PullRequests[0] = original with { Head = new string('e', 40) };
            runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate(),
                pollInterval: TimeSpan.Zero);
            results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.Contains("Repair attempt limit reached", results.Single().Detail);
            Assert.AreEqual(2, services.Repairs);
            Assert.AreEqual(2, new StateStore(path).State.PullRequests[original.Key].Attempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Brings a stale Dependabot branch up to date before spending a repair, while repairing foreign branches in place.
    /// </summary>
    /// <param name="ownership">The branch ownership name.</param>
    /// <param name="comments">The expected Dependabot command, or an empty string.</param>
    /// <param name="repairs">The expected repair count.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("Dependabot", "@dependabot rebase", 0)]
    [DataRow("Foreign", "", 1)]
    public async Task StaleFailingBranchIsUpdatedBeforeRepair(string ownership, string comments, int repairs)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = Enum.Parse<BranchOwnership>(ownership), Behind = true };
            services.PullRequests.Add(TestData.PullRequest() with
            {
                MergeState = "BLOCKED",
                Checks = [new CheckSnapshot("tests", "FAILURE", "")]
            });
            services.OnComment = (_, _) =>
            {
                services.Behind = false;
                services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
            };
            services.OnRepair = _ =>
            {
                services.PullRequests[0] = TestData.PullRequest() with { Head = new string('d', 40) };
                return new RepairResult(services.PullRequests[0].Head, "Repaired.");
            };
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(comments, string.Join(",", services.CommentBodies));
            Assert.AreEqual(repairs, services.Repairs);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Reports missing dependency review as a blocker for attention without recording a repair verdict or commenting.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnavailableDependencyReviewIsReportedForAttention()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest());
            var age = new ReleaseAgeGate((_, _) => throw new DependencyReviewUnavailableException(),
                (_, _) => throw new InvalidOperationException("Unexpected publication request."));
            var path = Path.Join(directory, "state.json");
            var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), age);
            var results = await runner.RunAsync(TestData.Settings() with { ReleaseAge = new ReleaseAgePolicy() },
                testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.Contains("dependency graph", results.Single().Detail);
            Assert.AreEqual(0, services.Comments);
            Assert.AreEqual(0, services.Merges);
            Assert.IsEmpty(new StateStore(path).State.PullRequests);
        }
        finally { Directory.Delete(directory, true); }
    }
}
