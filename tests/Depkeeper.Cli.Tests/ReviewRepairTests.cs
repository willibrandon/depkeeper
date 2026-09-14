namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that review feedback participates in every bounded repair cycle and final merge decision.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class ReviewRepairTests(TestContext testContext)
{
    /// <summary>
    /// Repairs a green PR with unresolved feedback and resolves the addressed thread after validation.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task GreenPrWithReviewFeedbackIsRepairedBeforeMerge()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-review-").FullName;
        try
        {
            var services = Services();
            services.ReviewThreads.Add(Thread("comment-1"));
            services.OnRepair = pr =>
            {
                services.PullRequests[0] = pr with { Head = new string('b', 40) };
                return new RepairResult(new string('b', 40), "Addressed review.", ["src/file.cs"]);
            };
            var runner = Runner(directory, services);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.AreEqual(1, services.Repairs);
            Assert.AreEqual("thread-1", services.ResolvedThreads.Single());
            Assert.Contains("untrusted-review-comments", services.LastRepairLogs!);
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Rechecks after a push and gives a newly added comment to the next repair instead of resolving it.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task NewCommentAfterPushRequiresAnotherRepair()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-review-").FullName;
        try
        {
            var services = Services();
            services.ReviewThreads.Add(Thread("comment-1"));
            services.OnReviewThreads = count =>
            {
                if (count == 2) services.ReviewThreads[0] = Thread("comment-2");
                return services.ReviewThreads.ToArray();
            };
            services.OnRepair = pr =>
            {
                var head = new string((char)('a' + services.Repairs), 40);
                services.PullRequests[0] = pr with { Head = head };
                return new RepairResult(head, "Addressed latest review.", ["src/file.cs"]);
            };
            var runner = Runner(directory, services);
            var results = await runner.RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("merged", results.Single().Outcome);
            Assert.AreEqual(2, services.Repairs);
            Assert.Contains("comment-2", services.LastRepairLogs!);
            Assert.AreEqual("thread-1", services.ResolvedThreads.Single());
        }
        finally { Directory.Delete(directory, true); }
    }

    /// <summary>
    /// Leaves review feedback unresolved when the validated repair did not change its referenced file.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnchangedReviewedPathBlocksMerge()
    {
        var directory = Directory.CreateTempSubdirectory("depkeeper-review-").FullName;
        try
        {
            var services = Services();
            services.ReviewThreads.Add(Thread("comment-1"));
            services.OnRepair = pr =>
            {
                var head = new string((char)('a' + services.Repairs), 40);
                services.PullRequests[0] = pr with { Head = head };
                return new RepairResult(head, "Changed another file.", ["src/other.cs"]);
            };
            var results = await Runner(directory, services).RunAsync(TestData.Settings(), testContext.CancellationToken);
            Assert.AreEqual("blocked", results.Single().Outcome);
            Assert.AreEqual(0, services.Merges);
            Assert.IsEmpty(services.ResolvedThreads);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static FakeMaintenanceServices Services()
    {
        var services = new FakeMaintenanceServices();
        services.PullRequests.Add(TestData.PullRequest());
        return services;
    }

    private static MaintenanceRunner Runner(string directory, FakeMaintenanceServices services) =>
        new(services, services, new StateStore(Path.Join(directory, "state.json")), new Redactor(), TestData.AgeGate(),
            pollInterval: TimeSpan.Zero);

    private static ReviewThread Thread(string comment) => new("thread-1", "src/file.cs", 10, comment,
        "reviewer: Please update this code. " + comment, "https://github.com/owner/repository/pull/1#discussion_r1");
}
