namespace Depkeeper.Cli;

/// <summary>
/// Captures a GitHub check associated with the current pull request head.
/// </summary>
/// <param name="Name">The check context.</param>
/// <param name="State">The normalized GitHub result or running state.</param>
/// <param name="Url">The check details URL.</param>
internal sealed record CheckSnapshot(string Name, string State, string Url);
