namespace Depkeeper.Cli;

/// <summary>
/// Captures a subprocess result without writing its output to public logs.
/// </summary>
/// <param name="ExitCode">The process exit status.</param>
/// <param name="Output">Bounded standard output.</param>
/// <param name="Error">Bounded standard error.</param>
internal sealed record CommandResult(int ExitCode, string Output, string Error);
