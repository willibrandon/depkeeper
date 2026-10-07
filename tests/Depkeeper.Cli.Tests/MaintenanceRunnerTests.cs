namespace Depkeeper.Cli.Tests;

/// <summary>
/// Exercises maintenance decisions without model requests or GitHub writes.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class MaintenanceRunnerTests(TestContext testContext)
{
    /// <summary>
    /// Supplies the previous independent verification failure when an operator retries a blocked revision.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task ExplicitRetryIncludesIndependentVerificationFeedback()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var pr = TestData.PullRequest() with { Checks = [new CheckSnapshot("tests", "FAILURE", "")] };
            services.PullRequests.Add(pr);
            var store = new StateStore(Path.Join(directory, "state.json"));
            store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "npm audit still reports vulnerable qs.", DateTimeOffset.UtcNow));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate());
            await runner.RunAsync(TestData.Settings() with { RetryBlocked = true }, testContext.CancellationToken);
            Assert.IsNotNull(services.LastRepairLogs);
            Assert.Contains("npm audit still reports vulnerable qs.", services.LastRepairLogs);
            Assert.IsNotNull(services.LastRepairEvidence);
            Assert.DoesNotContain("npm audit still reports vulnerable qs.", services.LastRepairEvidence.CurrentFailures);
            Assert.Contains("npm audit still reports vulnerable qs.", services.LastRepairEvidence.Context);
            Assert.AreEqual(1, services.Repairs);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Reports unfinished GitHub calculations as pending rather than actionable blockers.
    /// </summary>
    /// <param name="check">The observed check state.</param>
    /// <param name="mergeable">The observed mergeability calculation.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("IN_PROGRESS", "MERGEABLE")]
    [DataRow("SUCCESS", "UNKNOWN")]
    public async Task DryRunDistinguishesPendingCiFromFailures(string check, string mergeable)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest() with
            {
                Checks = [new CheckSnapshot("tests", check, "")],
                Mergeable = mergeable
            });
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(true), testContext.CancellationToken);
            Assert.AreEqual("pending", results.Single().Outcome);
            Assert.AreEqual(0, services.Merges);
            Assert.AreEqual(0, services.Repairs);
        }
        finally { Directory.Delete(directory, true); }
    }

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
            var path = Path.Join(directory, "state.json");
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
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
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
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
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
            var state = new StateStore(Path.Join(directory, "state.json"));
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(true), testContext.CancellationToken);
            Assert.AreEqual("would-merge", results.Single().Outcome);
            Assert.AreEqual(0, services.Merges);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual(0, services.Comments);
            Assert.IsFalse(File.Exists(Path.Join(directory, "state.json")));
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
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
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
            var path = Path.Join(directory, "state.json");
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
    /// Brings a Dependabot-owned branch up to date by asking Dependabot to rebase instead of pushing a merge commit.
    /// </summary>
    /// <param name="mergeState">GitHub's merge gate for the stale branch.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow("BEHIND")]
    [DataRow("DIRTY")]
    public async Task StaleDependabotBranchIsRebasedByDependabot(string mergeState)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            services.PullRequests.Add(TestData.PullRequest() with { MergeState = mergeState, Mergeable = "CONFLICTING" });
            services.OnComment = (_, _) => services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("@dependabot rebase", services.CommentBodies.Single());
            Assert.AreEqual(1, services.Merges);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Updates a stale Dependabot branch before evaluating publication metadata for its refreshed dependency revision.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task StaleBranchIsUpdatedBeforePublicationAgeCheck()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var original = TestData.PullRequest() with { MergeState = "BEHIND" };
            var updatedHead = new string('b', 40);
            services.PullRequests.Add(original);
            services.OnComment = (_, _) => services.PullRequests[0] = TestData.PullRequest() with { Head = updatedHead };
            var inspectedHeads = new List<string>();
            var age = new ReleaseAgeGate((pullRequest, _) =>
            {
                inspectedHeads.Add(pullRequest.Head);
                return Task.FromResult<IReadOnlyList<DependencyChange>>([new("added", "npm", "example", "2.0.0", [])]);
            }, (_, _) => Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow.AddDays(-30)));
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), age, pollInterval: TimeSpan.Zero);

            var results = await runner.RunAsync(TestData.Settings() with { ReleaseAge = new ReleaseAgePolicy() },
                testContext.CancellationToken);

            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.IsNotEmpty(inspectedHeads);
            Assert.IsTrue(inspectedHeads.All(head => head == updatedHead));
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Reuses the branch-update path when GitHub reports a base movement only during final verification.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task FinalRefreshBehindStateIsUpdatedBeforeMerge()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var original = TestData.PullRequest();
            services.PullRequests.Add(original);
            services.OnRefresh = (current, count) => count == 2 ? current with { MergeState = "BEHIND" } : current;
            services.OnComment = (_, _) => services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);

            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("@dependabot rebase", services.CommentBodies.Single());
            Assert.AreEqual(1, services.Merges);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Keeps merging the base into branches that carry a controller repair, which Dependabot would otherwise discard.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RepairedBranchIsUpdatedWithoutAskingDependabot()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = BranchOwnership.Foreign };
            services.PullRequests.Add(TestData.PullRequest() with { MergeState = "BEHIND" });
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
                new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.Contains("could not update the branch", results.Single().Detail);
            Assert.DoesNotContain("@dependabot rebase", services.CommentBodies);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Rechecks a blocked revision without a repair once its base moves, merging when the rebased head passes.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task BlockedRevisionIsRebasedAndRecheckedWhenBaseMoves()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var pr = TestData.PullRequest() with
            {
                MergeState = "DIRTY",
                Mergeable = "CONFLICTING",
                BaseHead = new string('f', 40),
                Checks = [new CheckSnapshot("tests", "FAILURE", "")]
            };
            services.PullRequests.Add(pr);
            services.Ownership = BranchOwnership.DependabotWithMerges;
            services.OnComment = (_, _) => services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
            var store = new StateStore(Path.Join(directory, "state.json"));
            store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Independent validation failed.", DateTimeOffset.UtcNow,
                BaseHead: new string('e', 40)));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("@dependabot recreate", services.CommentBodies.Single());
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual(1, services.Merges);
            Assert.AreEqual("merged", results.Single().Outcome);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Spends no repair on a rebased head that still fails, and keeps the earlier blocker visible.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RebasedBlockedRevisionThatStillFailsIsNotRepaired()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var pr = TestData.PullRequest() with
            {
                Head = new string('b', 40),
                MergeState = "BLOCKED",
                Checks = [new CheckSnapshot("tests", "FAILURE", "")]
            };
            services.PullRequests.Add(pr);
            var path = Path.Join(directory, "state.json");
            var store = new StateStore(path);
            store.Set(pr.Key, new AttemptState(new string('a', 40), 1, true, "Independent validation failed.", DateTimeOffset.UtcNow,
                BaseHead: pr.BaseHead, RebaseRequested: true));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.Contains("Independent validation failed.", results.Single().Detail);
            var saved = new StateStore(path).State.PullRequests[pr.Key];
            Assert.AreEqual(pr.Head, saved.Head);
            Assert.IsTrue(saved.Blocked);
            Assert.AreEqual(1, saved.Attempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Leaves a blocked revision alone while its base is unchanged, even when the branch is stale.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task BlockedRevisionWithUnchangedBaseIsNotRebased()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            // The PR's recorded base is stale by design; only the live branch tip decides whether the base moved.
            var pr = TestData.PullRequest() with { MergeState = "BEHIND", BaseHead = new string('e', 40) };
            services.PullRequests.Add(pr);
            var store = new StateStore(Path.Join(directory, "state.json"));
            store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Independent validation failed.", DateTimeOffset.UtcNow,
                BaseHead: services.BaseHead));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.AreEqual("Independent validation failed.", results.Single().Detail);
            Assert.AreEqual(0, services.Comments);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Reports an unanswered rebase request as pending, asks only once per base, and escalates after a day.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnansweredRebaseRequestIsPendingThenEscalates()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var pr = TestData.PullRequest() with { MergeState = "BEHIND", BaseHead = new string('e', 40) };
            services.PullRequests.Add(pr);
            var path = Path.Join(directory, "state.json");
            var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate(),
                pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("pending", results.Single().Outcome);
            var saved = new StateStore(path).State.PullRequests[pr.Key];
            Assert.IsTrue(saved.RebaseRequested);
            Assert.AreEqual(services.BaseHead, saved.BaseHead);
            runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate(),
                pollInterval: TimeSpan.Zero);
            results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("pending", results.Single().Outcome);
            Assert.AreEqual(1, services.Comments);
            var stale = new StateStore(path);
            stale.Set(pr.Key, saved with { UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2) });
            runner = new MaintenanceRunner(services, services, stale, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.Contains("Dependabot has not rebased", results.Single().Detail);
            Assert.AreEqual(0, services.Merges);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Waits for GitHub to finish recomputing mergeability before deciding whether a blocked revision is stale.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task BlockedRevisionWaitsForMergeabilityBeforeDeciding()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var pr = TestData.PullRequest() with
            {
                MergeState = "UNKNOWN",
                Mergeable = "UNKNOWN",
                Checks = [new CheckSnapshot("tests", "FAILURE", "")]
            };
            services.PullRequests.Add(pr);
            services.OnRefresh = (current, count) => count >= 3 && current.Head == pr.Head
                ? current with { MergeState = "DIRTY", Mergeable = "CONFLICTING" } : current;
            services.OnComment = (_, _) => services.PullRequests[0] = TestData.PullRequest() with { Head = new string('b', 40) };
            var store = new StateStore(Path.Join(directory, "state.json"));
            store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Independent validation failed.", DateTimeOffset.UtcNow,
                BaseHead: new string('e', 40)));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate(), pollInterval: TimeSpan.Zero);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("@dependabot rebase", services.CommentBodies.Single());
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.AreEqual(0, services.Repairs);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Records the live base tip with a blocked verdict so a later base move is detectable.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task BlockedVerdictRecordsTheLiveBaseTip()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { BaseHead = new string('9', 40) };
            var pr = TestData.PullRequest() with { MergeState = "BLOCKED", Checks = [new CheckSnapshot("tests", "FAILURE", "")] };
            services.PullRequests.Add(pr);
            var path = Path.Join(directory, "state.json");
            var runner = new MaintenanceRunner(services, services, new StateStore(path), new Redactor(), TestData.AgeGate());
            await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            var saved = new StateStore(path).State.PullRequests[pr.Key];
            Assert.IsTrue(saved.Blocked);
            Assert.AreEqual(new string('9', 40), saved.BaseHead);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Describes the pending branch update in inspection mode without posting a comment.
    /// </summary>
    /// <param name="owned">Whether Dependabot still owns the branch.</param>
    /// <param name="blocked">Whether the revision was blocked against an earlier base.</param>
    /// <param name="expected">The expected explanation.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(true, false, "Ask Dependabot to rebase and rerun CI.")]
    [DataRow(false, false, "Bring the branch up to date and rerun CI.")]
    [DataRow(true, true, "The base changed since this revision was blocked. Ask Dependabot to rebase and rerun CI.")]
    public async Task DryRunDescribesBranchUpdates(bool owned, bool blocked, string expected)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices { Ownership = owned ? BranchOwnership.Dependabot : BranchOwnership.Foreign };
            var pr = TestData.PullRequest() with { MergeState = "BEHIND", BaseHead = new string('f', 40) };
            services.PullRequests.Add(pr);
            var store = new StateStore(Path.Join(directory, "state.json"));
            if (blocked)
                store.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Independent validation failed.", DateTimeOffset.UtcNow,
                    BaseHead: new string('e', 40)));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(true), testContext.CancellationToken);
            Assert.AreEqual("would-update", results.Single().Outcome);
            Assert.AreEqual(expected, results.Single().Detail);
            Assert.AreEqual(0, services.Comments);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Retires the recorded verdict of a PR that was closed without merging, and reports it so its issue can close.
    /// </summary>
    /// <param name="dryRun">Whether state writes are disabled.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ClosedPullRequestRetiresItsBlockedState(bool dryRun)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-test-").FullName;
        try
        {
            var services = new FakeMaintenanceServices();
            var closed = TestData.PullRequest(2) with { State = "CLOSED", MergeState = "DIRTY", Mergeable = "CONFLICTING" };
            services.PullRequests.AddRange([TestData.PullRequest(), closed]);
            var path = Path.Join(directory, "state.json");
            var store = new StateStore(path);
            store.Set(closed.Key, new AttemptState(closed.Head, 1, true, "Independent validation failed.", DateTimeOffset.UtcNow));
            var runner = new MaintenanceRunner(services, services, store, new Redactor(), TestData.AgeGate());
            var results = await runner.RunAsync(TestData.Settings(dryRun), testContext.CancellationToken);
            var retired = results.Single(entry => entry.Number == 2);
            Assert.AreEqual("closed", retired.Outcome);
            Assert.Contains("closed without merging", retired.Detail);
            Assert.AreEqual(!dryRun, !new StateStore(path).State.PullRequests.ContainsKey(closed.Key));
            Assert.AreEqual(0, services.Comments);
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
            var runner = new MaintenanceRunner(services, services, new StateStore(Path.Join(directory, "state.json")),
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
