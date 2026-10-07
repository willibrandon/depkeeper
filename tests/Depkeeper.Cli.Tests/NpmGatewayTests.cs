using System.Text;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies npm lockfile fallback when GitHub dependency review is unavailable or incomplete.
/// </summary>
/// <param name="testContext">The current test context.</param>
[TestClass]
public sealed class NpmGatewayTests(TestContext testContext)
{
    /// <summary>
    /// Reads exact npm changes from the base and head when GitHub returns no usable dependency records.
    /// </summary>
    /// <param name="dependencyReviewExitCode">Whether GitHub returned an empty response or an error.</param>
    /// <returns>The test task.</returns>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    public async Task FallsBackToPackageLock(int dependencyReviewExitCode)
    {
        const string before = """{"packages":{"node_modules/example":{"version":"1.0.0"}}}""";
        const string after = """{"packages":{"node_modules/example":{"version":"1.1.0"}}}""";
        var gateway = Gateway(dependencyReviewExitCode, "package-lock.json", "modified", before, after);

        var changes = await gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken);

        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Version == "1.0.0", changes);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Version == "1.1.0", changes);
    }

    /// <summary>
    /// Fails closed when GitHub dependency review fails and no supported file establishes exact versions.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnavailableReviewWithoutFallbackFailsClosed()
    {
        var gateway = Gateway(1, "package.json", "modified", null, null);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken));
    }

    /// <summary>
    /// Rejects a changed npm lock that cannot be read at the exact PR head.
    /// </summary>
    /// <returns>The test task.</returns>
    [TestMethod]
    public async Task UnreadableHeadLockFailsClosed()
    {
        const string before = """{"packages":{"node_modules/example":{"version":"1.0.0"}}}""";
        var gateway = Gateway(0, "package-lock.json", "modified", before, null);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            gateway.GetDependencyChangesAsync(PullRequest(), testContext.CancellationToken));
    }

    private static PullRequestSnapshot PullRequest() => TestData.PullRequest() with { BaseHead = "base-head" };

    private static GitHubGateway Gateway(int reviewExitCode, string path, string status, string? before, string? after) =>
        new("", new Redactor(), (arguments, _, _) =>
        {
            var endpoint = arguments[1];
            if (endpoint.Contains("dependency-graph", StringComparison.Ordinal))
                return Task.FromResult(new CommandResult(reviewExitCode, "[]", "Forbidden"));
            if (endpoint.Contains("/pulls/1/files", StringComparison.Ordinal))
                return Result("[[{\"filename\":\"" + path + "\",\"status\":\"" + status + "\"}]]");
            var content = endpoint.Contains("ref=base-head", StringComparison.Ordinal) ? before : after;
            return content is null ? Task.FromResult(new CommandResult(1, "", "Not Found")) : Result(Content(content));
        });

    private static string Content(string value) =>
        "{\"encoding\":\"base64\",\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "\"}";

    private static Task<CommandResult> Result(string output) => Task.FromResult(new CommandResult(0, output, ""));
}
