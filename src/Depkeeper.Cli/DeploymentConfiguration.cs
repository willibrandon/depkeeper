namespace Depkeeper.Cli;

/// <summary>
/// Holds optional deployment configuration loaded from a JSON file.
/// </summary>
/// <param name="Model">The default Copilot model.</param>
/// <param name="Repositories">Repositories selected for maintenance.</param>
/// <param name="Profiles">Repository-specific validation profiles.</param>
/// <param name="ReleaseAge">Publication-age policy for the deployment.</param>
internal sealed record DeploymentConfiguration(string Model = "auto", string[]? Repositories = null,
    Dictionary<string, RepositoryProfile>? Profiles = null, ReleaseAgePolicy? ReleaseAge = null);
