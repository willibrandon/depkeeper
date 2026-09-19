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
    private readonly Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? _docker;
    private readonly Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? _pypi;
    private readonly Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? _hex;

    /// <summary>
    /// Creates a publication reader without GitHub or model credentials.
    /// </summary>
    /// <param name="client">A credential-free HTTP client.</param>
    /// <param name="actions">Optional GitHub Actions publication lookup.</param>
    /// <param name="docker">Optional Docker Hub digest publication lookup.</param>
    /// <param name="pypi">Optional exact PyPI publication lookup.</param>
    /// <param name="hex">Optional checksum-verified Hex publication lookup.</param>
    internal PublicationClient(HttpClient client,
        Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? actions = null,
        Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? docker = null,
        Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? pypi = null,
        Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>>? hex = null)
    {
        _client = client;
        _actions = actions;
        _docker = docker;
        _pypi = pypi;
        _hex = hex;
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
        if (dependency.Ecosystem == "docker") return _docker is null ? null : await _docker(dependency, cancellationToken);
        if (dependency.Ecosystem == "hex") return _hex is null ? null : await _hex(dependency, cancellationToken);
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
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return _cache[key] = await GetPyPiFallbackAsync(system, dependency, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var published = document.RootElement.TryGetProperty("publishedAt", out var value) &&
            value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var date)
            ? date : (DateTimeOffset?)null;
        if (published is null) published = await GetPyPiFallbackAsync(system, dependency, cancellationToken);
        _cache[key] = published;
        return published;
    }

    private Task<DateTimeOffset?> GetPyPiFallbackAsync(string system, DependencyChange dependency,
        CancellationToken cancellationToken) => system == "PYPI" && _pypi is not null
            ? _pypi(dependency, cancellationToken)
            : Task.FromResult<DateTimeOffset?>(null);
}
