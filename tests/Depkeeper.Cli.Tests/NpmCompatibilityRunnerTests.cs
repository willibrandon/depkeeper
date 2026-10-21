using System.Net;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies durable upstream waiting and automatic reconsideration without recurring model attempts.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class NpmCompatibilityRunnerTests(TestContext testContext)
{
    /// <summary>
    /// Recognizes an existing rejected repair, waits through incompatible releases, and resumes on a compatible stable release.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task CompatiblePublicationReopensOnlyTheUpstreamBlocker()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-upstream-").FullName;
        try
        {
            var (services, state) = Create(directory);
            var pr = services.PullRequests[0];
            state.Set(pr.Key, new AttemptState(pr.Head, 1, true, "Repair reverted the Dependabot dependency update: typescript.",
                DateTimeOffset.UtcNow));
            var registry = NpmCompatibilityFixture.Registry();
            using var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(registry));
            using var client = new HttpClient(handler);
            var compatibility = NpmCompatibilityFixture.Gate(client);
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(),
                compatibility: compatibility);

            var blocked = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            state = new StateStore(Path.Join(directory, "state.json"));
            Assert.AreEqual("upstream", blocked.Single().Outcome);
            Assert.IsNotNull(state.State.PullRequests[pr.Key].UpstreamConflict);
            Assert.AreEqual(1, state.State.PullRequests[pr.Key].Attempts);
            registry = NpmCompatibilityFixture.Registry(("0.9.11", "^5.0.0 || ^6.0.0", false));
            runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(), compatibility: compatibility);
            await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual(0, services.Repairs);

            registry = NpmCompatibilityFixture.Registry(("0.9.12", "^6.0.0 || ^7.0.0", false));
            services.OnRepair = current =>
            {
                Assert.AreEqual(1, state.State.PullRequests[pr.Key].Attempts);
                Assert.IsNotNull(services.LastRepairEvidence);
                Assert.Contains("@astrojs/check@0.9.12", services.LastRepairEvidence.Context);
                services.PullRequests[0] = current with { Head = new string('b', 40), Checks = TestData.PullRequest().Checks };
                return new RepairResult(services.PullRequests[0].Head, "Verified compatible upgrade.");
            };
            runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(),
                pollInterval: TimeSpan.Zero, compatibility: compatibility);
            var resumed = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("merged", resumed.Single().Outcome);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual(1, services.Merges);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Spends no repair attempts when the conflict is known, and keeps dry runs free of state writes.
    /// </summary>
    /// <param name="dryRun">Whether persistence is prohibited.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KnownConflictDoesNotInvokeTheRepairAgent(bool dryRun)
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-upstream-").FullName;
        try
        {
            var (services, state) = Create(directory);
            using var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(NpmCompatibilityFixture.Registry()));
            using var client = new HttpClient(handler);
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(),
                compatibility: NpmCompatibilityFixture.Gate(client));

            var result = await runner.RunAsync(TestData.Settings(dryRun), testContext.CancellationToken);

            Assert.AreEqual("upstream", result.Single().Outcome);
            Assert.Contains("Upstream npm compatibility blocker", result.Single().Detail);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual(0, services.Merges);
            Assert.AreEqual(!dryRun, File.Exists(Path.Join(directory, "state.json")));
            if (!dryRun) Assert.AreEqual(0, state.State.PullRequests.Values.Single().Attempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Keeps a known blocker closed to repairs during registry outages instead of treating missing evidence as a fix.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task RegistryOutageDoesNotResetTheRepairBudget()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-upstream-").FullName;
        try
        {
            var (services, state) = Create(directory);
            var unavailable = false;
            using var handler = new TestHttpMessageHandler(_ => unavailable ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) :
                TestHttpMessageHandler.Json(NpmCompatibilityFixture.Registry()));
            using var client = new HttpClient(handler);
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(),
                compatibility: NpmCompatibilityFixture.Gate(client));
            await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            unavailable = true;
            var result = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("upstream", result.Single().Outcome);
            Assert.Contains("could not be refreshed", result.Single().Detail);
            Assert.AreEqual(0, services.Repairs);
            Assert.IsNotNull(state.State.PullRequests.Values.Single().UpstreamConflict);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Does not reopen an unrelated terminal repair failure merely because a compatible npm version is available.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task CompatibleMetadataDoesNotResetUnrelatedBlockedHeads()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-upstream-").FullName;
        try
        {
            var (services, state) = Create(directory);
            var pr = services.PullRequests[0];
            state.Set(pr.Key, new AttemptState(pr.Head, 2, true, "Picket rejected the candidate.", DateTimeOffset.UtcNow));
            var registry = NpmCompatibilityFixture.Registry();
            using var handler = new TestHttpMessageHandler(_ => TestHttpMessageHandler.Json(registry));
            using var client = new HttpClient(handler);
            var runner = new MaintenanceRunner(services, services, state, new Redactor(), TestData.AgeGate(),
                compatibility: NpmCompatibilityFixture.Gate(client));

            await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.IsNull(state.State.PullRequests[pr.Key].UpstreamConflict);
            registry = NpmCompatibilityFixture.Registry(("0.9.12", "^7.0.0", false));
            var result = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);

            Assert.AreEqual("blocked", result.Single().Outcome);
            Assert.AreEqual(0, services.Repairs);
            Assert.AreEqual(2, state.State.PullRequests[pr.Key].Attempts);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (FakeMaintenanceServices Services, StateStore State) Create(string directory)
    {
        var services = new FakeMaintenanceServices { FailureLogs = NpmCompatibilityFixture.Logs };
        services.PullRequests.Add(NpmCompatibilityFixture.PullRequest() with
        {
            Checks = [new CheckSnapshot("documentation", "FAILURE", "")]
        });
        return (services, new StateStore(Path.Join(directory, "state.json")));
    }
}
