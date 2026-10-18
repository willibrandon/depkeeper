namespace Depkeeper.Cli;

/// <summary>
/// Records whether a verified peer conflict has a published stable upgrade path.
/// </summary>
/// <param name="Conflict">The exact manifest and registry evidence.</param>
/// <param name="CompatibleVersion">A stable requiring-package version supporting the selected peer, or null.</param>
internal sealed record NpmCompatibilityAssessment(NpmPeerConflict Conflict, string? CompatibleVersion);
