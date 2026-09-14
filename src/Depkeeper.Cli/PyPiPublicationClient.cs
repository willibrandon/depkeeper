using System.Text;
using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Reads exact Python release upload timestamps from PyPI's public metadata.
/// </summary>
internal sealed class PyPiPublicationClient
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, DateTimeOffset?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a PyPI metadata reader without registry credentials.
    /// </summary>
    /// <param name="client">The credential-free HTTP client.</param>
    internal PyPiPublicationClient(HttpClient client) => _client = client;

    /// <summary>
    /// Returns the first upload time only when PyPI identifies the exact requested release.
    /// </summary>
    /// <param name="dependency">The concrete Python package change.</param>
    /// <param name="cancellationToken">Cancels registry access.</param>
    /// <returns>The earliest artifact upload time, or null for unsupported or mismatched records.</returns>
    internal async Task<DateTimeOffset?> GetAsync(DependencyChange dependency, CancellationToken cancellationToken)
    {
        if (dependency.Ecosystem is not ("pip" or "pypi")) return null;
        var key = Normalize(dependency.Name) + "/" + dependency.Version;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var url = $"https://pypi.org/pypi/{Uri.EscapeDataString(dependency.Name)}/{Uri.EscapeDataString(dependency.Version)}/json";
        using var response = await _client.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return _cache[key] = null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("info", out var info) ||
            !info.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
            Normalize(name.GetString()!) != Normalize(dependency.Name) ||
            !info.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != dependency.Version ||
            !document.RootElement.TryGetProperty("urls", out var urls) || urls.ValueKind != JsonValueKind.Array)
            return _cache[key] = null;
        var uploads = urls.EnumerateArray().Select(UploadTime).OfType<DateTimeOffset>().ToArray();
        return _cache[key] = uploads.Length == 0 ? null : uploads.Min();
    }

    private static DateTimeOffset? UploadTime(JsonElement artifact) =>
        artifact.TryGetProperty("upload_time_iso_8601", out var value) && value.ValueKind == JsonValueKind.String &&
        value.TryGetDateTimeOffset(out var timestamp) ? timestamp : null;

    private static string Normalize(string value)
    {
        var result = new StringBuilder(value.Length);
        var separator = false;
        foreach (var character in value)
        {
            if (character is '-' or '_' or '.')
            {
                if (!separator) result.Append('-');
                separator = true;
            }
            else
            {
                result.Append(char.ToLowerInvariant(character));
                separator = false;
            }
        }
        return result.ToString();
    }
}
