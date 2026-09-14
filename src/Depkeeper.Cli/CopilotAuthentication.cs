using GitHub.Copilot;

namespace Depkeeper.Cli;

/// <summary>
/// Selects explicit automation credentials or the SDK's signed-in-user authentication.
/// </summary>
internal static class CopilotAuthentication
{
    /// <summary>
    /// Creates authentication options while allowing local GitHub login reuse when no token is supplied.
    /// </summary>
    /// <param name="token">An optional token supplied by the automation environment.</param>
    /// <param name="localCliPath">An optional installed CLI path for signed-in-user authentication.</param>
    /// <returns>Client options for the selected authentication method.</returns>
    internal static CopilotClientOptions CreateOptions(string? token, string? localCliPath = null)
    {
        var hasToken = !string.IsNullOrWhiteSpace(token);
        var cliPath = hasToken ? null : localCliPath ?? FindMacCli();
        return new CopilotClientOptions
        {
            GitHubToken = hasToken ? token : null,
            UseLoggedInUser = !hasToken,
            Connection = cliPath is null ? null : RuntimeConnection.ForStdio(cliPath)
        };
    }

    private static string? FindMacCli()
    {
        if (!OperatingSystem.IsMacOS() || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPILOT_CLI_PATH")))
        {
            return null;
        }

        // A stable installed executable retains its macOS Keychain authorization across rebuilds.
        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified).Select(directory => Path.Join(directory, "copilot")).FirstOrDefault(File.Exists);
    }
}
