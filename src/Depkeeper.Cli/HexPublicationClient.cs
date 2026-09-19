using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Verifies locked Hex release checksums and returns their registry-recorded publication timestamps.
/// </summary>
internal sealed partial class HexPublicationClient
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, DateTimeOffset?> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Creates a hex.pm metadata reader without registry credentials.
    /// </summary>
    /// <param name="client">The credential-free HTTP client.</param>
    internal HexPublicationClient(HttpClient client) => _client = client;

    /// <summary>
    /// Gets the release time only when hex.pm reports the exact locked version and checksum for a current release.
    /// </summary>
    /// <param name="dependency">The concrete Hex package change.</param>
    /// <param name="cancellationToken">Cancels registry access.</param>
    /// <returns>The release publication time, or null for private, retired, unlocked, or mismatched records.</returns>
    internal async Task<DateTimeOffset?> GetAsync(DependencyChange dependency, CancellationToken cancellationToken)
    {
        if (dependency.Ecosystem != "hex" || dependency.Checksum is null || !PublicPackage().IsMatch(dependency.Name)) return null;
        var key = dependency.Name + "/" + dependency.Version + "/" + dependency.Checksum;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var url = $"https://hex.pm/api/packages/{Uri.EscapeDataString(dependency.Name)}/releases/" +
            Uri.EscapeDataString(dependency.Version);
        using var response = await _client.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return _cache[key] = null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var release = document.RootElement;
        if (!release.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.String ||
            version.GetString() != dependency.Version ||
            !release.TryGetProperty("checksum", out var checksum) || checksum.ValueKind != JsonValueKind.String ||
            !checksum.GetString()!.Equals(dependency.Checksum, StringComparison.OrdinalIgnoreCase) ||
            !release.TryGetProperty("retirement", out var retirement) || retirement.ValueKind != JsonValueKind.Null)
            return _cache[key] = null;
        var timestamp = release.TryGetProperty("inserted_at", out var inserted) && inserted.ValueKind == JsonValueKind.String &&
            inserted.TryGetDateTimeOffset(out var value) ? value : (DateTimeOffset?)null;
        return _cache[key] = timestamp;
    }

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PublicPackage();
}
