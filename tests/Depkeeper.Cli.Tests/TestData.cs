namespace Depkeeper.Cli.Tests;

/// <summary>
/// Creates synthetic GitHub data without requiring credentials or external services.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// Creates a mergeable Dependabot pull request with a successful test check.
    /// </summary>
    /// <param name="number">The pull request number.</param>
    /// <returns>A synthetic snapshot.</returns>
    internal static PullRequestSnapshot PullRequest(int number = 1) => new("owner/repository", number, "Update dependency",
        "app/dependabot", "dependabot/example", new string('a', 40), "main", false, false, "MERGEABLE", "CLEAN", "",
        [new CheckSnapshot("tests", "SUCCESS", "https://github.com/owner/repository/actions/runs/123/job/456", DateTimeOffset.UtcNow)]);

    /// <summary>
    /// Creates bounded settings with publication lookups disabled for controller-only tests.
    /// </summary>
    /// <param name="dryRun">Whether writes are disabled.</param>
    /// <returns>The synthetic settings.</returns>
    internal static RunSettings Settings(bool dryRun = false) => new("auto", ["owner/repository"],
        new Dictionary<string, RepositoryProfile> { ["owner/repository"] = new(AutoRecover: false) }, dryRun, false,
        3, 2, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), null, new ReleaseAgePolicy(0));

    /// <summary>
    /// Creates an age gate whose disabled policy never calls a remote service.
    /// </summary>
    /// <returns>An isolated publication-age gate.</returns>
    internal static IReleaseAgeGate AgeGate() => new ReleaseAgeGate(
        (_, _) => throw new InvalidOperationException("Unexpected dependency request."),
        (_, _) => throw new InvalidOperationException("Unexpected publication request."));
}
