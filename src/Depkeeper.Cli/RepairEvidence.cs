namespace Depkeeper.Cli;

/// <summary>
/// Separates current failed-check evidence from review and historical repair context.
/// </summary>
/// <param name="CurrentFailures">Logs retrieved from failing checks on the exact current head.</param>
/// <param name="Context">Untrusted review feedback and historical verification details.</param>
internal sealed record RepairEvidence(string CurrentFailures, string Context = "")
{
    /// <summary>
    /// Gets all untrusted evidence supplied to the repair agent.
    /// </summary>
    internal string Prompt => CurrentFailures + Context;

    /// <summary>
    /// Gets whether the exact current failed-check evidence contains an npm audit report.
    /// </summary>
    internal bool HasNpmAuditReport => CurrentFailures.Contains("# npm audit report", StringComparison.OrdinalIgnoreCase);
}
