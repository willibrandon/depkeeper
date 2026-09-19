namespace Depkeeper.Cli;

/// <summary>
/// Applies publication-age policy to independently retrieved dependency metadata.
/// </summary>
internal sealed class ReleaseAgeGate : IReleaseAgeGate
{
    private readonly Func<PullRequestSnapshot, CancellationToken, Task<IReadOnlyList<DependencyChange>>> _changes;
    private readonly Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>> _publication;
    private readonly TimeProvider _clock;
    private readonly Func<PullRequestSnapshot, CancellationToken, Task<bool>>? _codeOnly;

    /// <summary>
    /// Creates an age gate with replaceable metadata sources.
    /// </summary>
    /// <param name="changes">Retrieves GitHub's dependency diff.</param>
    /// <param name="publication">Retrieves actual package publication timestamps.</param>
    /// <param name="clock">An optional clock for boundary tests.</param>
    /// <param name="codeOnly">Optional independent confirmation of code-only managed recovery changes.</param>
    internal ReleaseAgeGate(Func<PullRequestSnapshot, CancellationToken, Task<IReadOnlyList<DependencyChange>>> changes,
        Func<DependencyChange, CancellationToken, Task<DateTimeOffset?>> publication, TimeProvider? clock = null,
        Func<PullRequestSnapshot, CancellationToken, Task<bool>>? codeOnly = null)
    {
        _changes = changes;
        _publication = publication;
        _clock = clock ?? TimeProvider.System;
        _codeOnly = codeOnly;
    }

    /// <summary>
    /// Holds fresh or unverifiable dependencies and recognizes metadata-backed security fixes.
    /// </summary>
    /// <param name="pullRequest">The exact revision to inspect.</param>
    /// <param name="policy">The applicable cooldown policy.</param>
    /// <param name="cancellationToken">Cancels metadata retrieval.</param>
    /// <returns>A hold reason or null.</returns>
    public async Task<string?> GetBlockerAsync(PullRequestSnapshot pullRequest, ReleaseAgePolicy policy,
        CancellationToken cancellationToken)
    {
        if (policy.MinimumDays == 0) return null;
        try
        {
            var changes = await _changes(pullRequest, cancellationToken);
            if (changes.Count == 0 && pullRequest.ManagedRecovery && _codeOnly is not null &&
                await _codeOnly(pullRequest, cancellationToken)) return null;
            if (changes.Count == 0)
                return policy.AllowUnknown ? null :
                    "GitHub returned no dependency changes; this update's publication age cannot be verified.";
            var added = changes.Where(change => change.ChangeType == "added")
                .GroupBy(change => (change.Ecosystem, change.Name, change.Version, change.Checksum))
                .Select(group => group.First() with { Advisories = group.SelectMany(change => change.Advisories).Distinct().ToArray() })
                .ToArray();
            if (added.Length == 0) return null;
            var securityFix = added.Any(update => changes.Any(change => change.ChangeType == "removed" &&
                change.Ecosystem == update.Ecosystem && change.Name == update.Name && change.Advisories.Length > 0)) &&
                added.All(change => change.Advisories.Length == 0);
            if (securityFix && policy.SecurityFixesBypass) return null;
            foreach (var dependency in added)
            {
                var published = await _publication(dependency, cancellationToken);
                if (published is null)
                {
                    if (!policy.AllowUnknown)
                        return $"Publication date unavailable: {dependency.Ecosystem}/{dependency.Name}@{dependency.Version}.";
                    continue;
                }
                var eligible = published.Value.AddDays(policy.MinimumDays);
                if (_clock.GetUtcNow() < eligible)
                    return $"Cooling down: {dependency.Name}@{dependency.Version} becomes eligible {eligible:u}.";
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException or System.Text.Json.JsonException or
            System.Xml.XmlException)
        {
            return policy.AllowUnknown ? null :
                "Dependency publication lookup failed; automatic merging is held until metadata is available.";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return policy.AllowUnknown ? null : "Publication lookup timed out; automatic merging is held.";
        }
    }
}
