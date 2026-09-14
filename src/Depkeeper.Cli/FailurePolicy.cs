namespace Depkeeper.Cli;

/// <summary>
/// Keeps operational reporting boundaries from swallowing fatal runtime failures.
/// </summary>
internal static class FailurePolicy
{
    /// <summary>
    /// Determines whether an exception can be reported while continuing controlled maintenance.
    /// </summary>
    /// <param name="exception">The failure raised by an operation.</param>
    /// <returns>Whether the exception is suitable for normal error handling.</returns>
    internal static bool CanReport(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
