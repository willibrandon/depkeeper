namespace Depkeeper.Cli;

/// <summary>
/// Captures a concrete dependency change reported by GitHub dependency review.
/// </summary>
/// <param name="ChangeType">Whether the dependency was added or removed.</param>
/// <param name="Ecosystem">The package ecosystem.</param>
/// <param name="Name">The package name.</param>
/// <param name="Version">The concrete package version.</param>
/// <param name="Advisories">Known vulnerability identifiers associated with this version.</param>
/// <param name="Checksum">The locked content checksum, when the lock file records one for registry verification.</param>
internal sealed record DependencyChange(string ChangeType, string Ecosystem, string Name, string Version, string[] Advisories,
    string? Checksum = null);
