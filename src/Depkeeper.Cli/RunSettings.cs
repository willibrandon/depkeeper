using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Defines a bounded maintenance sweep and its deployment configuration.
/// </summary>
/// <param name="Model">The selected Copilot model ID.</param>
/// <param name="Repositories">The explicit repository allowlist.</param>
/// <param name="Profiles">Per-repository validation settings.</param>
/// <param name="DryRun">Whether all mutations and inference are disabled.</param>
/// <param name="RetryBlocked">Whether an operator requested another attempt on blocked heads.</param>
/// <param name="MaxRepairs">The maximum repair sessions in one sweep.</param>
/// <param name="MaxAttempts">The maximum attempts for an unchanged lineage.</param>
/// <param name="RepairTimeout">The time budget for each repair session.</param>
/// <param name="CiTimeout">The time budget for waiting on GitHub checks.</param>
/// <param name="OnlyPullRequest">An optional PR number for focused operation.</param>
/// <param name="ReleaseAge">The default publication-age policy.</param>
internal sealed partial record RunSettings(string Model, string[] Repositories, Dictionary<string, RepositoryProfile> Profiles,
    bool DryRun, bool RetryBlocked, int MaxRepairs, int MaxAttempts, TimeSpan RepairTimeout, TimeSpan CiTimeout, int? OnlyPullRequest,
    ReleaseAgePolicy? ReleaseAge = null)
{
    /// <summary>
    /// Loads optional JSON configuration with environment and CLI overrides.
    /// </summary>
    /// <param name="path">An optional configuration file.</param>
    /// <param name="model">An optional explicit model.</param>
    /// <param name="repositories">Optional explicit repositories.</param>
    /// <param name="dryRun">Whether this is a nonmutating inspection.</param>
    /// <param name="retryBlocked">Whether blocked heads may be retried.</param>
    /// <param name="maxRepairs">The sweep's maximum repair sessions.</param>
    /// <param name="maxAttempts">The attempt budget per pull request lineage.</param>
    /// <param name="repairMinutes">Minutes allowed for one repair.</param>
    /// <param name="ciMinutes">Minutes allowed for CI to finish.</param>
    /// <param name="onlyPullRequest">An optional selected pull request.</param>
    /// <param name="minimumAgeDays">An optional publication-age override.</param>
    /// <param name="allowUnknownAge">Whether unknown publication dates are explicitly permitted.</param>
    /// <param name="waitForSecurityFixes">Whether security fixes must also complete the cooldown.</param>
    /// <returns>The validated run settings.</returns>
    internal static RunSettings Load(string? path, string? model, string[] repositories, bool dryRun, bool retryBlocked,
        int maxRepairs, int maxAttempts, int repairMinutes, int ciMinutes, int? onlyPullRequest,
        int? minimumAgeDays = null, bool allowUnknownAge = false, bool waitForSecurityFixes = false)
    {
        var file = path ?? (File.Exists("depkeeper.json") ? "depkeeper.json" : null);
        var configuration = file is null ? new DeploymentConfiguration() :
            JsonSerializer.Deserialize(File.ReadAllText(file), JsonContext.Default.DeploymentConfiguration)
                ?? throw new InvalidDataException("Invalid deployment configuration.");
        var environment = Environment.GetEnvironmentVariable("DEPKEEPER_REPOSITORIES");
        var selected = repositories.Length > 0 ? repositories : string.IsNullOrWhiteSpace(environment) ? configuration.Repositories ?? [] :
            JsonSerializer.Deserialize(environment, JsonContext.Default.StringArray) ?? [];
        if (selected.Length == 0 || selected.Any(repository => string.IsNullOrWhiteSpace(repository) ||
            !RepositoryName().IsMatch(repository)))
            throw new InvalidDataException(
                "Select repositories with --repository, DEPKEEPER_REPOSITORIES, or depkeeper.json using OWNER/REPO names.");
        if (maxRepairs is < 0 or > 100 || maxAttempts is < 1 or > 5 || repairMinutes is < 1 or > 120 || ciMinutes is < 1 or > 60)
            throw new InvalidDataException("Limits must be: repairs 0-100, attempts 1-5, repair minutes 1-120, CI minutes 1-60.");
        if (onlyPullRequest is not null && (onlyPullRequest < 1 || selected.Length != 1))
            throw new InvalidDataException("--pr requires a positive PR number and exactly one repository.");
        var profiles = new Dictionary<string, RepositoryProfile>(configuration.Profiles ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles.Values)
        {
            if (!ImageName().IsMatch(profile.Image) || profile.Verify is { Length: 0 } ||
                (profile.Install ?? []).Any(string.IsNullOrWhiteSpace) || (profile.Verify ?? []).Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Profiles require a valid container image and nonempty command arrays when specified.");
            if ((profile.RequiredChecks ?? []).Concat(profile.AdvisoryChecks ?? []).Concat(profile.PostMergeChecks ?? [])
                .Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("Configured check names must not be empty.");
        }
        var age = configuration.ReleaseAge ?? new ReleaseAgePolicy();
        age = age with
        {
            MinimumDays = minimumAgeDays ?? age.MinimumDays,
            AllowUnknown = allowUnknownAge || age.AllowUnknown,
            SecurityFixesBypass = !waitForSecurityFixes && age.SecurityFixesBypass
        };
        if (age.MinimumDays is < 0 or > 365 || profiles.Values.Any(profile => profile.ReleaseAge?.MinimumDays is < 0 or > 365))
            throw new InvalidDataException("Minimum release age must be between 0 and 365 days.");
        var selectedModel = model ?? Environment.GetEnvironmentVariable("DEPKEEPER_MODEL") ?? configuration.Model;
        if (string.IsNullOrWhiteSpace(selectedModel)) selectedModel = "auto";
        return new RunSettings(selectedModel,
            selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), profiles, dryRun, retryBlocked, maxRepairs, maxAttempts,
            TimeSpan.FromMinutes(repairMinutes), TimeSpan.FromMinutes(ciMinutes), onlyPullRequest, age);
    }

    /// <summary>
    /// Validates a GitHub repository name before passing it to external commands.
    /// </summary>
    /// <param name="repository">The repository identifier.</param>
    /// <returns>Whether the identifier uses OWNER/REPO format.</returns>
    internal static bool IsRepository(string repository) => RepositoryName().IsMatch(repository);

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.:/@-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex ImageName();
}
