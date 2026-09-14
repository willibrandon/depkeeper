namespace Depkeeper.Cli;

/// <summary>
/// Evaluates merge eligibility independently of agent output.
/// </summary>
internal static class MergePolicy
{
    /// <summary>
    /// Identifies pull requests owned by Dependabot in an allowed repository.
    /// </summary>
    /// <param name="pullRequest">The current pull request.</param>
    /// <returns>Whether automatic maintenance is eligible.</returns>
    internal static bool IsEligible(PullRequestSnapshot pullRequest) =>
        pullRequest.Author is "app/dependabot" or "dependabot[bot]" &&
        !pullRequest.Draft && !pullRequest.CrossRepository && pullRequest.State == "OPEN";

    /// <summary>
    /// Returns a blocking reason, or null when every required merge condition passes.
    /// </summary>
    /// <param name="pullRequest">The fresh pull request snapshot.</param>
    /// <param name="expectedHead">The exact revision inspected by the controller.</param>
    /// <param name="profile">Repository-specific check policy.</param>
    /// <returns>The first unmet merge requirement.</returns>
    internal static string? GetBlocker(PullRequestSnapshot pullRequest, string expectedHead, RepositoryProfile profile)
    {
        if (!IsEligible(pullRequest)) return "Only non-draft, same-repository Dependabot PRs are eligible.";
        if (pullRequest.Head != expectedHead) return "The PR changed during maintenance; inspect the new revision.";
        if (pullRequest.Mergeable != "MERGEABLE") return "GitHub has not confirmed the branch is conflict-free.";
        if (pullRequest.ReviewDecision is "CHANGES_REQUESTED" or "REVIEW_REQUIRED") return "A required review is outstanding.";
        if (pullRequest.MergeState is not ("CLEAN" or "HAS_HOOKS" or "UNSTABLE"))
            return $"GitHub merge gate: {pullRequest.MergeState}.";
        var checks = pullRequest.Checks.Where(check =>
            !(profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal)).ToArray();
        if (!checks.Any(check => check.State == "SUCCESS")) return "No successful CI checks were reported for this revision.";
        foreach (var name in profile.RequiredChecks ?? [])
        {
            if (!checks.Any(check => check.Name == name && check.State == "SUCCESS"))
                return $"Required check is missing or unsuccessful: {name}.";
        }
        if (checks.Any(check => check.State is not ("SUCCESS" or "NEUTRAL" or "SKIPPED"))) return "CI has failed or is still running.";
        return null;
    }

    /// <summary>
    /// Determines whether a current check needs remediation.
    /// </summary>
    /// <param name="pullRequest">The current pull request.</param>
    /// <param name="profile">Repository-specific advisory checks.</param>
    /// <returns>Whether a non-advisory check failed.</returns>
    internal static bool HasFailure(PullRequestSnapshot pullRequest, RepositoryProfile profile) =>
        pullRequest.Checks.Any(check => check.State is "FAILURE" or "ERROR" or "TIMED_OUT" or "ACTION_REQUIRED" or "CANCELLED" &&
            !(profile.AdvisoryChecks ?? []).Contains(check.Name, StringComparer.Ordinal));

    /// <summary>
    /// Determines whether all reported checks have finished.
    /// </summary>
    /// <param name="pullRequest">The current pull request.</param>
    /// <returns>Whether at least one check is present and none remain pending.</returns>
    internal static bool ChecksFinished(PullRequestSnapshot pullRequest) => pullRequest.Checks.Count > 0 &&
        pullRequest.Checks.All(check => check.State is "SUCCESS" or "NEUTRAL" or "SKIPPED" or "FAILURE" or "ERROR" or
            "TIMED_OUT" or "ACTION_REQUIRED" or "CANCELLED");
}
