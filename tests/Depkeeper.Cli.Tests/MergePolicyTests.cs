namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies that agent claims cannot bypass deterministic merge requirements.
/// </summary>
[TestClass]
public sealed class MergePolicyTests
{
    /// <summary>
    /// Requires successful checks on the exact inspected commit.
    /// </summary>
    [TestMethod]
    public void RequiresCurrentHeadAndActualSuccessfulChecks()
    {
        var pullRequest = TestData.PullRequest();
        Assert.IsNull(MergePolicy.GetBlocker(pullRequest, pullRequest.Head, new RepositoryProfile()));
        Assert.IsNotNull(MergePolicy.GetBlocker(pullRequest, "old-head", new RepositoryProfile()));
        Assert.IsNotNull(MergePolicy.GetBlocker(pullRequest with { Checks = [] }, pullRequest.Head, new RepositoryProfile()));
        var skipped = pullRequest with { Checks = [new CheckSnapshot("tests", "SKIPPED", "")] };
        Assert.IsNotNull(MergePolicy.GetBlocker(skipped, pullRequest.Head, new RepositoryProfile()));
    }

    /// <summary>
    /// Blocks failed, pending, cancelled, and missing mandatory checks.
    /// </summary>
    /// <param name="state">The unsuccessful check state.</param>
    [TestMethod]
    [DataRow("FAILURE")]
    [DataRow("IN_PROGRESS")]
    [DataRow("CANCELLED")]
    [DataRow("TIMED_OUT")]
    public void BlocksUnsuccessfulChecks(string state)
    {
        var pullRequest = TestData.PullRequest() with { Checks = [new CheckSnapshot("tests", state, "")] };
        Assert.IsNotNull(MergePolicy.GetBlocker(pullRequest, pullRequest.Head, new RepositoryProfile()));
        pullRequest = TestData.PullRequest();
        Assert.IsNotNull(MergePolicy.GetBlocker(pullRequest, pullRequest.Head,
            new RepositoryProfile(RequiredChecks: ["missing"])));
    }

    /// <summary>
    /// Restricts writes to eligible Dependabot PRs without outstanding GitHub merge gates.
    /// </summary>
    [TestMethod]
    public void BlocksDraftsForksOtherAuthorsAndReviewRequirements()
    {
        var pullRequest = TestData.PullRequest();
        Assert.IsFalse(MergePolicy.IsEligible(pullRequest with { Draft = true }));
        Assert.IsFalse(MergePolicy.IsEligible(pullRequest with { CrossRepository = true }));
        Assert.IsFalse(MergePolicy.IsEligible(pullRequest with { Author = "another-user" }));
        Assert.IsFalse(MergePolicy.IsEligible(pullRequest with { State = "CLOSED" }));
        Assert.IsNotNull(MergePolicy.GetBlocker(pullRequest with { ReviewDecision = "CHANGES_REQUESTED" },
            pullRequest.Head, new RepositoryProfile()));
        Assert.IsNotNull(MergePolicy.GetBlocker(pullRequest with { MergeState = "BLOCKED" },
            pullRequest.Head, new RepositoryProfile()));
    }
}
