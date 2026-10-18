using System.Text.Json;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Supplies captured npm conflict shapes and independently controlled registry evidence.
/// </summary>
internal static class NpmCompatibilityFixture
{
    /// <summary>
    /// Gets a peer-resolution failure with GitHub CLI job-log prefixes.
    /// </summary>
    internal const string Logs = """
        Build documentation	UNKNOWN STEP	2026-10-16T14:34:22Z npm error code ERESOLVE
        Build documentation	UNKNOWN STEP	2026-10-16T14:34:22Z npm error Found: typescript@7.0.2
        Build documentation	UNKNOWN STEP	2026-10-16T14:34:22Z npm error peer typescript@"^5.0.0 || ^6.0.0" from @astrojs/check@0.9.10
        """;

    /// <summary>
    /// Gets the exact PR manifest selecting an incompatible stable peer.
    /// </summary>
    internal const string Manifest = """
        {"devDependencies":{"@astrojs/check":"0.9.10","typescript":"7.0.2"}}
        """;

    /// <summary>
    /// Creates a PR snapshot with an exact base revision available for dependency preservation checks.
    /// </summary>
    /// <returns>The exact-head snapshot.</returns>
    internal static PullRequestSnapshot PullRequest() => TestData.PullRequest() with { BaseHead = new string('c', 40) };

    /// <summary>
    /// Creates a complete registry response with optional independently published releases.
    /// </summary>
    /// <param name="releases">Additional versions, peer requirements, and deprecation flags.</param>
    /// <returns>The registry package document.</returns>
    internal static string Registry(params (string Version, string Range, bool Deprecated)[] releases)
    {
        var versions = new Dictionary<string, object>();
        foreach (var (version, range, deprecated) in releases.Prepend(("0.9.10", "^5.0.0 || ^6.0.0", false)))
            versions[version] = new
            {
                name = "@astrojs/check",
                version,
                peerDependencies = new Dictionary<string, string> { ["typescript"] = range },
                deprecated = deprecated ? "Deprecated" : ""
            };
        return JsonSerializer.Serialize(new { name = "@astrojs/check", versions });
    }

    /// <summary>
    /// Creates a compatibility gate whose repository reader returns the selected test manifest.
    /// </summary>
    /// <param name="client">The isolated registry transport.</param>
    /// <param name="manifest">An optional replacement manifest.</param>
    /// <returns>The configured compatibility gate.</returns>
    internal static NpmCompatibilityGate Gate(HttpClient client, string manifest = Manifest) => new(client, (_, _) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["docs-site/package.json"] = manifest
        }));
}
