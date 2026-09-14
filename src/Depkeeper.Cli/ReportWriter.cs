using System.Text;

namespace Depkeeper.Cli;

/// <summary>
/// Produces concise reports from controller-verified outcomes.
/// </summary>
internal static class ReportWriter
{
    /// <summary>
    /// Formats a maintenance summary as Markdown.
    /// </summary>
    /// <param name="entries">Verified run outcomes.</param>
    /// <param name="model">The configured model.</param>
    /// <param name="dryRun">Whether mutations were disabled.</param>
    /// <param name="redactor">The diagnostic redactor.</param>
    /// <returns>A sanitized report.</returns>
    internal static string Format(IReadOnlyList<ReportEntry> entries, string model, bool dryRun, Redactor redactor)
    {
        var text = new StringBuilder($"# Depkeeper{(dryRun ? " dry run" : string.Empty)}\n\n");
        text.AppendLine($"Updated: {DateTimeOffset.UtcNow:O} · Model: {Escape(model)}\n");
        text.AppendLine(string.Join(" · ", entries.GroupBy(entry => entry.Outcome).OrderBy(group => group.Key)
            .Select(group => $"{group.Key}: {group.Count()}")) + "\n");
        text.AppendLine("| Repository / PR | Outcome | Detail |\n| --- | --- | --- |");
        foreach (var entry in entries)
        {
            var url = "https://github.com/" + entry.Repository + (entry.Number > 0 ? "/pull/" + entry.Number : string.Empty);
            var label = entry.Repository + (entry.Number > 0 ? "#" + entry.Number : string.Empty);
            text.AppendLine($"| [{Escape(label)}]({url}) | {Escape(entry.Outcome)} | {Escape(redactor.Clean(entry.Detail))} |");
        }
        return redactor.Clean(text.ToString());
    }

    private static string Escape(string value) => value.Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)
        .Replace("`", "'", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal)
        .Replace("]", "\\]", StringComparison.Ordinal);
}
