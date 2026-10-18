using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Semver;

namespace Depkeeper.Cli;

/// <summary>
/// Verifies direct npm peer conflicts before spending repair attempts and rechecks upstream stable releases.
/// </summary>
internal sealed partial class NpmCompatibilityGate
{
    private readonly HttpClient _client;
    private readonly Func<PullRequestSnapshot, CancellationToken, Task<IReadOnlyDictionary<string, string>>> _manifests;

    /// <summary>
    /// Creates a compatibility reader using credential-free registry access and exact-head manifests.
    /// </summary>
    /// <param name="client">The public registry HTTP client.</param>
    /// <param name="manifests">The controller-owned reader for changed npm manifests.</param>
    internal NpmCompatibilityGate(HttpClient client,
        Func<PullRequestSnapshot, CancellationToken, Task<IReadOnlyDictionary<string, string>>> manifests)
    {
        _client = client;
        _manifests = manifests;
    }

    /// <summary>
    /// Uses ERESOLVE logs only as hints and verifies both declarations and published peer requirements.
    /// </summary>
    /// <param name="pullRequest">The exact revision associated with the failure logs.</param>
    /// <param name="logs">Untrusted current failed-check logs.</param>
    /// <param name="cancellationToken">Cancels repository and registry reads.</param>
    /// <returns>A verified assessment, or null when evidence is unsupported, incomplete, or unavailable.</returns>
    internal async Task<NpmCompatibilityAssessment?> CheckAsync(PullRequestSnapshot pullRequest, string logs,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!logs.Contains("ERESOLVE", StringComparison.Ordinal) || pullRequest.BaseHead is null) return null;
        var requirements = PeerRequirement().Matches(logs).Take(8).ToArray();
        if (requirements.Length == 0) return null;
        IReadOnlyDictionary<string, string> manifests;
        IReadOnlyDictionary<string, string> baseline;
        try
        {
            manifests = await _manifests(pullRequest, cancellationToken);
            baseline = await _manifests(pullRequest with { Head = pullRequest.BaseHead }, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or JsonException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
        NpmCompatibilityAssessment? compatible = null;
        foreach (var match in requirements)
        {
            var package = match.Groups["package"].Value;
            var version = match.Groups["version"].Value;
            var peer = match.Groups["peer"].Value;
            var range = match.Groups["range"].Value;
            var found = FoundPackage().Matches(logs).FirstOrDefault(value => value.Groups["package"].Value == peer);
            if (found is null) continue;
            var selected = found.Groups["version"].Value;
            foreach (var (path, content) in manifests)
            {
                if (!Declares(content, package, version, peer, selected) || !baseline.TryGetValue(path, out var before) ||
                    !Declares(before, package, version)) continue;
                var conflict = new NpmPeerConflict(path, package, version, peer, selected, range);
                var assessment = await AssessAsync(conflict, cancellationToken);
                if (assessment is null) continue;
                if (assessment.CompatibleVersion is null) return assessment;
                compatible ??= assessment;
            }
        }
        return compatible;
    }

    private async Task<NpmCompatibilityAssessment?> AssessAsync(NpmPeerConflict conflict, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://registry.npmjs.org/" + Uri.EscapeDataString(conflict.Package));
            request.Headers.Accept.ParseAdd("application/vnd.npm.install-v1+json");
            using var response = await _client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (Text(root, "name") != conflict.Package || !root.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Object || !versions.TryGetProperty(conflict.Version, out var current) ||
                Text(current, "name") != conflict.Package || Text(current, "version") != conflict.Version ||
                PeerRange(current, conflict.Peer) != conflict.Range ||
                !SemVersion.TryParse(conflict.Version, SemVersionStyles.Strict, out var minimum) || minimum.IsPrerelease ||
                !SemVersion.TryParse(conflict.PeerVersion, SemVersionStyles.Strict, out var selected) || selected.IsPrerelease ||
                SemVersionRange.ParseNpm(conflict.Range).Contains(selected)) return null;
            var supporting = new List<SemVersion>();
            var candidates = versions.EnumerateObject()
                .Select(entry => (Version: ParseVersion(entry.Name), Metadata: entry.Value))
                .Where(release => release.Version is not null && !release.Version.IsPrerelease &&
                    release.Version.ComparePrecedenceTo(minimum) >= 0)
                .Select(release => (Version: release.Version!, release.Metadata));
            foreach (var (candidate, metadata) in candidates)
            {
                if (Text(metadata, "name") != conflict.Package || Text(metadata, "version") != candidate.ToString()) return null;
                if (metadata.TryGetProperty("deprecated", out var deprecated) &&
                    deprecated.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(deprecated.GetString())) continue;
                var required = PeerRange(metadata, conflict.Peer);
                if (!metadata.TryGetProperty("peerDependencies", out var peers) ||
                    peers.ValueKind == JsonValueKind.Object && !peers.TryGetProperty(conflict.Peer, out _))
                    supporting.Add(candidate);
                else if (required is not null && SemVersionRange.ParseNpm(required).Contains(selected)) supporting.Add(candidate);
                else if (required is null) return null;
            }
            var version = supporting.Order(SemVersion.PrecedenceComparer).FirstOrDefault()?.ToString();
            return new NpmCompatibilityAssessment(conflict, version);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or ArgumentException or
            FormatException or OverflowException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static SemVersion? ParseVersion(string value) =>
        SemVersion.TryParse(value, SemVersionStyles.Strict, out var version) ? version : null;

    private static bool Declares(string content, string package, string version, string? peer = null, string? selected = null)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            return DirectVersion(root, package) == version && (peer is null || DirectVersion(root, peer) == selected);
        }
        catch (JsonException) { return false; }
    }

    private static string? DirectVersion(JsonElement manifest, string package)
    {
        string[] fields = ["dependencies", "devDependencies", "optionalDependencies"];
        var declarations = fields.Select(field => manifest.TryGetProperty(field, out var values) ? Text(values, package) : null)
            .Where(value => value is not null).Distinct(StringComparer.Ordinal).ToArray();
        return declarations.Length == 1 ? declarations[0] : null;
    }

    private static string? PeerRange(JsonElement metadata, string peer) =>
        metadata.TryGetProperty("peerDependencies", out var peers) ? Text(peers, peer) : null;

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    [GeneratedRegex("peer (?<peer>(?:@[a-z0-9._~-]+/)?[a-z0-9._~-]+)@\"(?<range>[^\"\\r\\n]{1,256})\" from " +
        @"(?<package>(?:@[a-z0-9._~-]+/)?[a-z0-9._~-]+)@(?<version>[0-9]+\.[0-9]+\.[0-9]+)(?![0-9A-Za-z.+-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex PeerRequirement();

    [GeneratedRegex(@"Found: (?<package>(?:@[a-z0-9._~-]+/)?[a-z0-9._~-]+)@(?<version>[0-9]+\.[0-9]+\.[0-9]+)" +
        @"(?![0-9A-Za-z.+-])", RegexOptions.CultureInvariant)]
    private static partial Regex FoundPackage();
}
