using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Verifies Docker Hub tag digests and returns their registry-recorded push timestamps.
/// </summary>
internal sealed class DockerHubPublicationClient
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, DateTimeOffset?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a Docker Hub metadata reader without registry credentials.
    /// </summary>
    /// <param name="client">The credential-free HTTP client.</param>
    internal DockerHubPublicationClient(HttpClient client) => _client = client;

    /// <summary>
    /// Gets the tag push time only when Docker Hub reports the exact dependency digest.
    /// </summary>
    /// <param name="dependency">The concrete Docker image change.</param>
    /// <param name="cancellationToken">Cancels registry access.</param>
    /// <returns>The digest-specific tag push time, or null for unsupported or mismatched records.</returns>
    internal async Task<DateTimeOffset?> GetAsync(DependencyChange dependency, CancellationToken cancellationToken)
    {
        if (!DockerImageReference.TryParse(dependency, out var reference) || reference!.Registry != "docker.io") return null;
        var key = reference.Name + ":" + reference.Version;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var url = $"https://hub.docker.com/v2/repositories/{Uri.EscapeDataString(reference.Namespace)}/" +
            $"{Uri.EscapeDataString(reference.Repository)}/tags/{Uri.EscapeDataString(reference.Tag)}";
        using var response = await _client.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return _cache[key] = null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var expected = "sha256:" + reference.Digest;
        if (!document.RootElement.TryGetProperty("digest", out var digest) || digest.GetString() != expected)
            return _cache[key] = null;
        var timestamp = document.RootElement.TryGetProperty("tag_last_pushed", out var pushed) &&
            pushed.ValueKind == JsonValueKind.String && pushed.TryGetDateTimeOffset(out var value) ? value : (DateTimeOffset?)null;
        return _cache[key] = timestamp;
    }
}
