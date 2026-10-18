namespace Depkeeper.Cli;

/// <summary>
/// Separates exact-head CI evidence from historical repair diagnostics in actionable reports.
/// </summary>
internal static class BlockerReport
{
    /// <summary>
    /// Describes current checks and merge gates before any earlier repair result.
    /// </summary>
    /// <param name="current">The fresh pull request revision.</param>
    /// <param name="profile">The trusted check policy.</param>
    /// <param name="history">An optional previous repair failure.</param>
    /// <param name="reviews">The number of unresolved review threads.</param>
    /// <param name="redactor">The diagnostic sanitizer.</param>
    /// <param name="upstream">An optional current registry-verified compatibility blocker.</param>
    /// <returns>A bounded explanation with clearly attributed history.</returns>
    internal static string Format(PullRequestSnapshot current, RepositoryProfile profile, string? history, int reviews,
        Redactor redactor, string? upstream = null)
    {
        var checks = current.Checks.Where(check => check.State is not ("SUCCESS" or "NEUTRAL") &&
            !(profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal))
            .Select(check => check.Name + " (" + check.State + ")").Distinct(StringComparer.Ordinal).Take(12);
        var details = string.Join(", ", checks);
        var gate = MergePolicy.GetBlocker(current, current.Head, profile);
        var reason = $"Current CI at {current.Head}: " + (details.Length > 0 ? details : "no unsuccessful checks reported") + ".";
        if (gate is not null) reason += " " + gate;
        if (reviews > 0) reason += $" {reviews} unresolved review thread(s).";
        if (upstream is not null) reason += " " + upstream;
        reason = redactor.Clean(reason);
        if (reason.Length > 900) reason = reason[..900];
        if (!string.IsNullOrWhiteSpace(history))
        {
            var previous = redactor.Clean(history);
            reason += " Previous repair result: " + (previous.Length > 550 ? previous[..120] + " [...] " + previous[^420..] : previous);
        }
        return reason;
    }
}
