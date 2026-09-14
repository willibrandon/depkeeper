using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Verifies Microsoft Container Registry tag digests through its public catalog metadata.
/// </summary>
internal sealed class McrPublicationClient
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, DateTimeOffset?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates an MCR metadata reader without registry credentials.
    /// </summary>
    /// <param name="client">The credential-free HTTP client.</param>
    internal McrPublicationClient(HttpClient client) => _client = client;

    /// <summary>
    /// Returns the tag update time only when MCR reports the exact requested digest.
    /// </summary>
    /// <param name="dependency">The concrete Docker image change.</param>
    /// <param name="cancellationToken">Cancels registry access.</param>
    /// <returns>The exact tag's last update time, or null for unsupported or mismatched records.</returns>
    internal async Task<DateTimeOffset?> GetAsync(DependencyChange dependency, CancellationToken cancellationToken)
    {
        if (!DockerImageReference.TryParse(dependency, out var reference) || reference!.Registry != "mcr.microsoft.com") return null;
        var key = reference.Name + ":" + reference.Version;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var path = Uri.EscapeDataString(reference.Namespace) + "/" + Uri.EscapeDataString(reference.Repository);
        using var response = await _client.GetAsync($"https://mcr.microsoft.com/api/v1/catalog/{path}/tags?reg=mar",
            cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return _cache[key] = null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var expected = "sha256:" + reference.Digest;
        var tags = document.RootElement.EnumerateArray().Where(item =>
            item.TryGetProperty("name", out var name) && name.GetString() == reference.Tag).Take(2).ToArray();
        if (tags.Length != 1 || !tags[0].TryGetProperty("digest", out var digest) ||
            digest.GetString() != expected) return _cache[key] = null;
        var timestamp = tags[0].TryGetProperty("lastModifiedDate", out var modified) && modified.ValueKind == JsonValueKind.String &&
            modified.TryGetDateTimeOffset(out var value) ? value : (DateTimeOffset?)null;
        return _cache[key] = timestamp;
    }
}
