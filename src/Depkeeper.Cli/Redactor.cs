using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Removes runtime credentials and common token forms from diagnostic text.
/// </summary>
internal sealed partial class Redactor
{
    private readonly string[] _secrets;

    /// <summary>
    /// Creates a redactor for the current execution's credentials.
    /// </summary>
    /// <param name="secrets">Credential values that must never reach reports.</param>
    internal Redactor(params string?[] secrets) => _secrets = secrets.Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!).Distinct(StringComparer.Ordinal).OrderByDescending(value => value.Length).ToArray();

    /// <summary>
    /// Sanitizes text before it enters an agent prompt or report.
    /// </summary>
    /// <param name="value">Potentially sensitive text.</param>
    /// <returns>Text with known credentials and token patterns removed.</returns>
    internal string Clean(string value)
    {
        foreach (var secret in _secrets) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = TerminalCodes().Replace(Tokens().Replace(value, "[REDACTED]"), string.Empty);
        return ControlCharacters().Replace(value, string.Empty);
    }

    [GeneratedRegex(@"(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|" +
        @"sk-(?:proj-|ant-)?[A-Za-z0-9_-]{24,}|https?://[^\s/@]+:[^\s/@]+@)", RegexOptions.CultureInvariant)]
    private static partial Regex Tokens();

    [GeneratedRegex(@"(?:\x1B\[|\x9B|\uFFFD\[)[0-?]*[ -/]*[@-~]", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalCodes();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacters();
}
