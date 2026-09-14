using System.Text.Json;

namespace Depkeeper.Cli;

/// <summary>
/// Reads public registry publication metadata through the deps.dev API.
/// </summary>
internal sealed class PublicationClient
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, DateTimeOffset?> _cache = new(StringComparer.Ordinal);
    private readonly Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? _actions;

    /// <summary>
    /// Creates a publication reader without GitHub or model credentials.
    /// </summary>
    /// <param name="client">A credential-free HTTP client.</param>
    /// <param name="actions">Optional GitHub Actions publication lookup.</param>
    internal PublicationClient(HttpClient client,
        Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? actions = null)
    {
        _client = client;
        _actions = actions;
    }

    /// <summary>
    /// Retrieves a concrete version's publication date from supported public ecosystems.
    /// </summary>
    /// <param name="dependency">The package version to inspect.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The published timestamp, or null for unsupported or missing metadata.</returns>
    internal async Task<DateTimeOffset?> GetAsync(DependencyChange dependency, CancellationToken cancellationToken)
    {
        if (dependency.Ecosystem is "actions" or "githubactions" or "github-actions")
            return _actions is null ? null : await _actions(dependency, cancellationToken);
        var system = dependency.Ecosystem.ToLowerInvariant() switch
        {
            "npm" => "NPM",
            "nuget" => "NUGET",
            "pip" or "pypi" => "PYPI",
            "cargo" => "CARGO",
            "go" or "gomod" => "GO",
            "maven" => "MAVEN",
            _ => null
        };
        if (system is null) return null;
        var key = $"{system}/{dependency.Name}/{dependency.Version}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var url = $"https://api.deps.dev/v3/systems/{system}/packages/{Uri.EscapeDataString(dependency.Name)}/versions/" +
            Uri.EscapeDataString(dependency.Version);
        using var response = await _client.GetAsync(url, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return _cache[key] = null;
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var published = document.RootElement.TryGetProperty("publishedAt", out var value) &&
            value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date)
            ? date : (DateTimeOffset?)null;
        _cache[key] = published;
        return published;
    }
}
