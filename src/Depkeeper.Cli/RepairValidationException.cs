namespace Depkeeper.Cli;

/// <summary>
/// Returns independently observed candidate failures to the controller for a bounded additional attempt.
/// </summary>
/// <param name="message">Sanitized feedback describing why the candidate was rejected.</param>
internal sealed class RepairValidationException(string message) : InvalidOperationException(message);
