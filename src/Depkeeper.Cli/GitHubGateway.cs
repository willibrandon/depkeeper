using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Uses the official GitHub CLI for discovery, checks, comments, and guarded merges.
/// </summary>
internal sealed partial class GitHubGateway : IGitHubGateway
{
    private const string Fields = "number,title,author,headRefName,headRefOid,baseRefName,isDraft,isCrossRepository," +
        "mergeable,mergeStateStatus,reviewDecision,statusCheckRollup,state";
    private readonly Dictionary<string, string?> _environment;
    private readonly Redactor _redactor;

    /// <summary>
    /// Creates a GitHub gateway using a deployment credential.
    /// </summary>
    /// <param name="token">The repository-maintenance credential.</param>
    /// <param name="redactor">The shared diagnostic redactor.</param>
    internal GitHubGateway(string token, Redactor redactor)
    {
        _environment = new Dictionary<string, string?> { ["GH_TOKEN"] = token, ["GH_PROMPT_DISABLED"] = "1", ["GH_PAGER"] = "cat" };
        _redactor = redactor;
    }

    /// <summary>
    /// Lists open same-repository Dependabot pull requests.
    /// </summary>
    /// <param name="repository">The repository name.</param>
    /// <param name="cancellationToken">Cancels discovery.</param>
    /// <returns>Eligible pull requests in number order.</returns>
    public async Task<IReadOnlyList<PullRequestSnapshot>> ListAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            ["pr", "list", "--repo", repository, "--state", "open", "--limit", "1000", "--json", Fields], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.EnumerateArray().Select(value => Parse(repository, value))
            .Where(MergePolicy.IsEligible).OrderBy(value => value.Number).ToArray();
    }

    /// <summary>
    /// Retrieves fresh pull request state directly from GitHub.
    /// </summary>
    /// <param name="pullRequest">The pull request identity.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The latest pull request state.</returns>
    public async Task<PullRequestSnapshot> RefreshAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(
            ["pr", "view", Number(pullRequest), "--repo", pullRequest.Repository, "--json", Fields], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        return Parse(pullRequest.Repository, document.RootElement);
    }

    /// <summary>
    /// Collects bounded failed-job logs without exposing credentials.
    /// </summary>
    /// <param name="pullRequest">The failing pull request.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>Sanitized failure details.</returns>
    public async Task<string> GetFailureLogsAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var runs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in pullRequest.Checks.Where(check => check.State is "FAILURE" or "ERROR" or "TIMED_OUT" or "ACTION_REQUIRED"))
        {
            text.AppendLine($"Check: {check.Name} ({check.State})");
            var match = RunUrl().Match(check.Url);
            if (!match.Success || !runs.Add(match.Groups[1].Value)) continue;
            var result = await ExecuteAsync(
                ["run", "view", match.Groups[1].Value, "--repo", pullRequest.Repository, "--log-failed"], cancellationToken);
            if (result.ExitCode == 0) text.AppendLine(result.Output.Length > 24000 ? result.Output[^24000..] : result.Output);
            if (runs.Count >= 4) break;
        }
        return _redactor.Clean(text.ToString());
    }

    /// <summary>
    /// Requests a merge-from-base update against an exact source revision.
    /// </summary>
    /// <param name="pullRequest">The expected revision.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Whether the update succeeded.</returns>
    public async Task<bool> UpdateBranchAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken) =>
        (await ExecuteAsync(["api", "--method", "PUT", $"repos/{pullRequest.Repository}/pulls/{Number(pullRequest)}/update-branch",
            "-f", "expected_head_sha=" + pullRequest.Head], cancellationToken)).ExitCode == 0;

    /// <summary>
    /// Squash-merges the exact verified head without administrator overrides.
    /// </summary>
    /// <param name="pullRequest">The verified pull request.</param>
    /// <param name="cancellationToken">Cancels the merge request.</param>
    /// <returns>Whether GitHub accepted the merge.</returns>
    public async Task<bool> MergeAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        var merge = await ExecuteAsync(["pr", "merge", Number(pullRequest), "--repo", pullRequest.Repository, "--squash",
            "--match-head-commit", pullRequest.Head], cancellationToken);
        if (merge.ExitCode != 0) return false;
        var confirmed = await ExecuteAsync(["pr", "view", Number(pullRequest), "--repo", pullRequest.Repository,
            "--json", "state", "--jq", ".state"], cancellationToken);
        return confirmed.ExitCode == 0 && confirmed.Output.Trim() == "MERGED";
    }

    /// <summary>
    /// Adds a sanitized comment to a pull request.
    /// </summary>
    /// <param name="pullRequest">The pull request identity.</param>
    /// <param name="body">The controller's report.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    public async Task CommentAsync(PullRequestSnapshot pullRequest, string body, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(["pr", "comment", Number(pullRequest), "--repo", pullRequest.Repository,
            "--body-file", "-"], cancellationToken, _redactor.Clean(body));
        RequireSuccess(result);
    }

    /// <summary>
    /// Updates one persistent daily report issue in the deployment repository.
    /// </summary>
    /// <param name="repository">The deployment repository.</param>
    /// <param name="body">The sanitized daily report.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    internal async Task PublishReportAsync(string repository, string body, CancellationToken cancellationToken)
    {
        const string title = "Depkeeper maintenance report";
        var result = await ExecuteAsync(["issue", "list", "--repo", repository, "--state", "open", "--search",
            title + " in:title", "--json", "number,title", "--limit", "100"], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        var issue = document.RootElement.EnumerateArray().FirstOrDefault(value => Text(value, "title") == title);
        string[] arguments = issue.ValueKind == JsonValueKind.Undefined
            ? ["issue", "create", "--repo", repository, "--title", title, "--body-file", "-"]
            : ["issue", "edit", issue.GetProperty("number").GetInt32().ToString(CultureInfo.InvariantCulture),
                "--repo", repository, "--body-file", "-"];
        RequireSuccess(await ExecuteAsync(arguments, cancellationToken, _redactor.Clean(body)));
    }

    /// <summary>
    /// Parses GitHub's polymorphic check results into controller-owned data.
    /// </summary>
    /// <param name="repository">The repository name.</param>
    /// <param name="value">The GitHub pull request object.</param>
    /// <returns>A normalized pull request.</returns>
    internal static PullRequestSnapshot Parse(string repository, JsonElement value)
    {
        var checks = new List<CheckSnapshot>();
        if (value.TryGetProperty("statusCheckRollup", out var rollup) && rollup.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in rollup.EnumerateArray())
            {
                var status = Text(check, "status");
                checks.Add(new CheckSnapshot(Text(check, "name", Text(check, "context")),
                    status.Length == 0 ? Text(check, "state") : status == "COMPLETED" ? Text(check, "conclusion") : status,
                    Text(check, "detailsUrl", Text(check, "targetUrl"))));
            }
        }
        return new PullRequestSnapshot(repository, value.GetProperty("number").GetInt32(), Text(value, "title"),
            value.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                ? Text(author, "login") : string.Empty,
            Text(value, "headRefName"), Text(value, "headRefOid"), Text(value, "baseRefName"),
            value.GetProperty("isDraft").GetBoolean(), value.GetProperty("isCrossRepository").GetBoolean(),
            Text(value, "mergeable"), Text(value, "mergeStateStatus"), Text(value, "reviewDecision"), checks,
            Text(value, "state", "OPEN"));
    }

    /// <summary>
    /// Retrieves concrete dependency changes and vulnerability metadata for the current revision.
    /// </summary>
    /// <param name="pullRequest">The exact pull request revision.</param>
    /// <param name="cancellationToken">Cancels metadata retrieval.</param>
    /// <returns>GitHub dependency review records.</returns>
    internal async Task<IReadOnlyList<DependencyChange>> GetDependencyChangesAsync(PullRequestSnapshot pullRequest,
        CancellationToken cancellationToken)
    {
        var comparison = Uri.EscapeDataString(pullRequest.BaseBranch + "..." + pullRequest.Head);
        var result = await ExecuteAsync(["api", $"repos/{pullRequest.Repository}/dependency-graph/compare/{comparison}"],
            cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.EnumerateArray().Select(value => new DependencyChange(Text(value, "change_type"),
            Text(value, "ecosystem"), Text(value, "name"), Text(value, "version"),
            value.GetProperty("vulnerabilities").EnumerateArray().Select(item => Text(item, "advisory_ghsa_id")).ToArray())).ToArray();
    }

    /// <summary>
    /// Matches an action's actual commit to GitHub's server-recorded release publication time.
    /// </summary>
    /// <param name="dependency">The action reference.</param>
    /// <param name="cancellationToken">Cancels metadata retrieval.</param>
    /// <returns>The matching publication time, or null when no release can be verified.</returns>
    internal async Task<DateTimeOffset?> GetActionPublicationAsync(DependencyChange dependency, CancellationToken cancellationToken)
    {
        var parts = dependency.Name.Split('/');
        if (parts.Length < 2 || !RunSettings.IsRepository(parts[0] + "/" + parts[1])) return null;
        var reference = Uri.EscapeDataString(dependency.Version);
        var commit = await ExecuteAsync(["api", $"repos/{parts[0]}/{parts[1]}/commits/{reference}", "--jq", ".sha"],
            cancellationToken);
        if (commit.ExitCode != 0) return null;
        const string query = """
            query($owner:String!,$name:String!) {
              repository(owner:$owner,name:$name) {
                releases(first:100,orderBy:{field:CREATED_AT,direction:DESC}) {
                  nodes { publishedAt tagCommit { oid } }
                }
              }
            }
            """;
        var releases = await ExecuteAsync(["api", "graphql", "-f", "query=" + query,
            "-f", "owner=" + parts[0], "-f", "name=" + parts[1]], cancellationToken);
        if (releases.ExitCode != 0) return null;
        using var document = JsonDocument.Parse(releases.Output);
        var dates = new List<DateTimeOffset>();
        foreach (var release in document.RootElement.GetProperty("data").GetProperty("repository")
            .GetProperty("releases").GetProperty("nodes").EnumerateArray())
        {
            if (release.TryGetProperty("tagCommit", out var tag) && tag.ValueKind == JsonValueKind.Object &&
                Text(tag, "oid") == commit.Output.Trim() &&
                release.GetProperty("publishedAt").ValueKind == JsonValueKind.String &&
                release.GetProperty("publishedAt").TryGetDateTimeOffset(out var published)) dates.Add(published);
        }
        return dates.Count == 0 ? null : dates.Min();
    }

    private Task<CommandResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null) =>
        ProcessRunner.RunAsync("gh", arguments, environment: _environment, input: input, cancellationToken: cancellationToken);

    private static string Text(JsonElement value, string name, string fallback = "") =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : fallback;

    private static string Number(PullRequestSnapshot pullRequest) => pullRequest.Number.ToString(CultureInfo.InvariantCulture);

    private static void RequireSuccess(CommandResult result)
    {
        if (result.ExitCode != 0) throw new IOException("GitHub operation failed; check repository access and current branch rules.");
    }

    [GeneratedRegex(@"/actions/runs/(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex RunUrl();
}
