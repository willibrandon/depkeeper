namespace Depkeeper.Cli;

/// <summary>
/// Identifies an npm peer incompatibility independently checked against the PR manifest and public registry.
/// </summary>
/// <param name="Manifest">The exact-head manifest declaring both packages.</param>
/// <param name="Package">The package imposing the peer requirement.</param>
/// <param name="Version">The selected version of the requiring package.</param>
/// <param name="Peer">The incompatible peer package.</param>
/// <param name="PeerVersion">The peer version selected by the PR.</param>
/// <param name="Range">The registry-confirmed peer requirement.</param>
internal sealed record NpmPeerConflict(string Manifest, string Package, string Version, string Peer, string PeerVersion, string Range)
{
    /// <summary>
    /// Gets the verified incompatibility without including untrusted build logs.
    /// </summary>
    internal string Summary => $"Upstream npm compatibility blocker in {Manifest}: {Package}@{Version} requires {Peer} {Range}, " +
        $"but the PR selects {PeerVersion}. No nondeprecated stable release at or above {Version} supports that peer version.";
}
