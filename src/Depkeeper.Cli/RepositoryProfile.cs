namespace Depkeeper.Cli;

/// <summary>
/// Configures a repository's isolated validation environment and mandatory checks.
/// </summary>
/// <param name="Image">The Docker image used for dependency installation and tests.</param>
/// <param name="Install">Optional trusted installation commands.</param>
/// <param name="Verify">Optional trusted verification commands.</param>
/// <param name="RequiredChecks">Additional mandatory GitHub check contexts.</param>
/// <param name="AdvisoryChecks">Nonblocking check contexts that must still be reported.</param>
/// <param name="ReleaseAge">An optional repository-specific publication-age policy.</param>
/// <param name="PostMergeChecks">Additional mandatory checks on the resulting merge commit.</param>
internal sealed record RepositoryProfile(string Image = "auto", string[]? Install = null,
    string[]? Verify = null, string[]? RequiredChecks = null, string[]? AdvisoryChecks = null, ReleaseAgePolicy? ReleaseAge = null,
    string[]? PostMergeChecks = null);
