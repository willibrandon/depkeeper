namespace Depkeeper.Cli;

/// <summary>
/// Defines publication-age requirements for automatic dependency maintenance and merging.
/// </summary>
/// <param name="MinimumDays">Days since publication required for ordinary updates.</param>
/// <param name="SecurityFixesBypass">Whether metadata-verified vulnerability fixes can bypass the delay.</param>
/// <param name="AllowUnknown">Whether an operator explicitly permits unverifiable publication dates.</param>
internal sealed record ReleaseAgePolicy(int MinimumDays = 3, bool SecurityFixesBypass = true, bool AllowUnknown = false);
