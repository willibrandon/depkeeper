using System.CommandLine;
using GitHub.Copilot;

namespace Depkeeper.Cli;

/// <summary>
/// Executes CLI commands and reports their process exit status.
/// </summary>
internal static class Commands
{
    /// <summary>
    /// Dispatches a command using the supplied output streams and credential source.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="output">The standard output writer.</param>
    /// <param name="error">The diagnostic output writer.</param>
    /// <param name="getCopilotToken">The deferred Copilot credential source.</param>
    /// <param name="cancellationToken">Cancels command execution.</param>
    /// <returns>Zero on success, one on operational failure, or two on invalid usage.</returns>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error,
        Func<string?> getCopilotToken, CancellationToken cancellationToken = default)
    {
        var root = new RootCommand("Dependency maintenance powered by GitHub Copilot.");
        var models = new Command("models", "List models available through your Copilot subscription.");
        models.SetAction((_, token) => ListModelsAsync(output, error, getCopilotToken, token));
        root.Subcommands.Add(models);
        var configuration = new InvocationConfiguration
        {
            Output = output,
            Error = error
        };
        var result = root.Parse(args.Length == 0 ? ["--help"] : args);
        var exitCode = await result.InvokeAsync(configuration, cancellationToken);
        return result.Errors.Count > 0 ? 2 : exitCode;
    }

    private static async Task<int> ListModelsAsync(TextWriter output, TextWriter error,
        Func<string?> getCopilotToken, CancellationToken cancellationToken)
    {
        try
        {
            await using var client = new CopilotClient(CopilotAuthentication.CreateOptions(getCopilotToken()));
            await client.StartAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var models = await client.ListModelsAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            foreach (var model in models)
            {
                await output.WriteLineAsync(model.Id);
            }
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception)
        {
            await error.WriteLineAsync("Could not load Copilot models. Check gh auth status and your Copilot access.");
            return 1;
        }
    }
}
