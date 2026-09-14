namespace Depkeeper.Cli;

/// <summary>
/// Maps controller outcomes to workflow status without hiding run-level failures.
/// </summary>
internal static class WorkflowExitCode
{
    /// <summary>
    /// Treats reported blockers as a completed sweep while preserving all other statuses.
    /// </summary>
    /// <param name="exitCode">The controller process exit code.</param>
    /// <returns>The workflow step exit code.</returns>
    internal static int Map(int exitCode) => exitCode == 2 ? 0 : exitCode;
}
