namespace Depkeeper.Cli;

/// <summary>
/// Captures a GitHub check associated with the current pull request head.
/// </summary>
/// <param name="Name">The check context.</param>
/// <param name="State">The normalized GitHub result or running state.</param>
/// <param name="Url">The check details URL.</param>
/// <param name="CompletedAt">The server-recorded completion time, when available.</param>
internal sealed record CheckSnapshot(string Name, string State, string Url, DateTimeOffset? CompletedAt = null)
{
    /// <summary>
    /// Gets whether the reported check has reached a terminal state.
    /// </summary>
    internal bool Finished => State is "SUCCESS" or "NEUTRAL" or "SKIPPED" or "FAILURE" or "ERROR" or
        "TIMED_OUT" or "ACTION_REQUIRED" or "CANCELLED";

    /// <summary>
    /// Gets whether the reported check ended unsuccessfully.
    /// </summary>
    internal bool Failed => Finished && State is not ("SUCCESS" or "NEUTRAL" or "SKIPPED");
}
