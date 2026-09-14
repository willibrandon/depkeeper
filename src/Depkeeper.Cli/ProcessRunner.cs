using System.Diagnostics;
using System.Text;

namespace Depkeeper.Cli;

/// <summary>
/// Runs argument-separated subprocesses with bounded output and cooperative cancellation.
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// Executes a process without shell interpolation or logging its arguments and environment.
    /// </summary>
    /// <param name="executable">The executable to run.</param>
    /// <param name="arguments">Arguments passed individually to the process.</param>
    /// <param name="directory">An optional working directory.</param>
    /// <param name="environment">Environment overrides; null values remove variables.</param>
    /// <param name="input">Optional standard input.</param>
    /// <param name="cancellationToken">Cancels the process and its descendants.</param>
    /// <returns>The bounded process result.</returns>
    internal static async Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        string? directory = null, IReadOnlyDictionary<string, string?>? environment = null, string? input = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            WorkingDirectory = directory ?? Environment.CurrentDirectory
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null) start.Environment.Remove(key);
            else start.Environment[key] = value;
        }
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        var output = CaptureAsync(process.StandardOutput, cancellationToken);
        var error = CaptureAsync(process.StandardError, cancellationToken);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
                process.StandardInput.Close();
            }
            await Task.WhenAll(output, error, process.WaitForExitAsync(cancellationToken));
            return new CommandResult(process.ExitCode, await output, await error);
        }
        catch
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<string> CaptureAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        const int limit = 2 * 1024 * 1024;
        var text = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            text.Append(buffer, 0, count);
            if (text.Length > limit) text.Remove(0, text.Length - limit);
        }
        return text.ToString();
    }
}
