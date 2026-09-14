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
        "mergeable,mergeStateStatus,reviewDecision,statusCheckRollup,state,mergeCommit";
    private readonly Dictionary<string, string?> _environment;
    private readonly Redactor _redactor;
    private readonly Func<IReadOnlyList<string>, CancellationToken, string?, Task<CommandResult>>? _execute;
    private string? _actor;

    /// <summary>
    /// Creates a GitHub gateway using a deployment credential.
    /// </summary>
    /// <param name="token">The repository-maintenance credential.</param>
    /// <param name="redactor">The shared diagnostic redactor.</param>
    /// <param name="execute">An optional transport for isolated integration tests.</param>
    internal GitHubGateway(string token, Redactor redactor,
        Func<IReadOnlyList<string>, CancellationToken, string?, Task<CommandResult>>? execute = null)
    {
        _environment = new Dictionary<string, string?> { ["GH_TOKEN"] = token, ["GH_PROMPT_DISABLED"] = "1", ["GH_PAGER"] = "cat" };
        _redactor = redactor;
        _execute = execute;
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
        var current = Parse(pullRequest.Repository, document.RootElement);
        return current with
        {
            ManagedRecovery = pullRequest.ManagedRecovery && current.Author == pullRequest.Author &&
                current.Branch == pullRequest.Branch && current.BaseBranch == pullRequest.BaseBranch && !current.CrossRepository
        };
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
            string[] selection = match.Groups[2].Success ? ["--job", match.Groups[2].Value, "--log"] : ["--log-failed"];
            var result = await ExecuteAsync(
                ["run", "view", match.Groups[1].Value, "--repo", pullRequest.Repository, .. selection], cancellationToken);
            if (result.ExitCode == 0)
                text.AppendLine(result.Output.Length > 24000 ? result.Output[..8000] + "\n[log truncated]\n" +
                    result.Output[^16000..] : result.Output);
            if (runs.Count >= 4) break;
        }
        return _redactor.Clean(text.ToString());
    }

    /// <summary>
    /// Retrieves unresolved inline review conversations through GitHub's review-thread API.
    /// </summary>
    /// <param name="pullRequest">The PR to inspect.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The bounded unresolved threads.</returns>
    public async Task<IReadOnlyList<ReviewThread>> GetReviewThreadsAsync(PullRequestSnapshot pullRequest,
        CancellationToken cancellationToken)
    {
        var (owner, name) = SplitRepository(pullRequest.Repository);
        const string query = """
            query($owner:String!,$name:String!,$number:Int!,$endCursor:String) {
              repository(owner:$owner,name:$name) {
                pullRequest(number:$number) {
                  reviewThreads(first:100,after:$endCursor) {
                    nodes {
                      id isResolved path line
                      comments(last:100) {
                        nodes { id body url author { login } }
                        pageInfo { hasPreviousPage }
                      }
                    }
                    pageInfo { hasNextPage endCursor }
                  }
                }
              }
            }
            """;
        var result = await ExecuteAsync(["api", "graphql", "--paginate", "--slurp", "-f", "query=" + query,
            "-f", "owner=" + owner, "-f", "name=" + name, "-F", "number=" + Number(pullRequest)], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        var threads = new List<ReviewThread>();
        foreach (var reviewThreads in document.RootElement.EnumerateArray().Select(page =>
            page.GetProperty("data").GetProperty("repository").GetProperty("pullRequest").GetProperty("reviewThreads")))
        {
            foreach (var thread in reviewThreads.GetProperty("nodes").EnumerateArray()
                .Where(thread => !thread.GetProperty("isResolved").GetBoolean()))
            {
                var comments = thread.GetProperty("comments");
                if (comments.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean())
                    throw new IOException("A review thread has more than 100 comments and requires manual triage.");
                var messages = comments.GetProperty("nodes").EnumerateArray().ToArray();
                if (messages.Length == 0) continue;
                var conversation = string.Join("\n", messages.Select(comment =>
                    (comment.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                        ? Text(author, "login", "unknown") : "unknown") + ": " + Text(comment, "body")));
                if (conversation.Length > 8000) conversation = conversation[^8000..];
                var latest = messages[^1];
                threads.Add(new ReviewThread(Text(thread, "id"), Text(thread, "path"),
                    thread.TryGetProperty("line", out var line) && line.ValueKind == JsonValueKind.Number ? line.GetInt32() : null,
                    Text(latest, "id"), conversation, Text(latest, "url")));
            }
        }
        return threads;
    }

    /// <summary>
    /// Resolves unchanged review threads after the controller confirms the exact repaired revision.
    /// </summary>
    /// <param name="pullRequest">The exact repaired revision.</param>
    /// <param name="threadIds">The addressed thread identifiers.</param>
    /// <param name="cancellationToken">Cancels mutations.</param>
    public async Task ResolveReviewThreadsAsync(PullRequestSnapshot pullRequest, IReadOnlyList<string> threadIds,
        CancellationToken cancellationToken)
    {
        var current = await RefreshAsync(pullRequest, cancellationToken);
        if (current.Head != pullRequest.Head) throw new IOException("The PR changed before review threads could be resolved.");
        const string mutation = """
            mutation($threadId:ID!) {
              resolveReviewThread(input:{threadId:$threadId}) { thread { id isResolved } }
            }
            """;
        foreach (var id in threadIds.Distinct(StringComparer.Ordinal))
        {
            var result = await ExecuteAsync(["api", "graphql", "-f", "query=" + mutation, "-f", "threadId=" + id],
                cancellationToken);
            RequireSuccess(result);
            using var document = JsonDocument.Parse(result.Output);
            var thread = document.RootElement.GetProperty("data").GetProperty("resolveReviewThread").GetProperty("thread");
            if (Text(thread, "id") != id || !thread.GetProperty("isResolved").GetBoolean())
                throw new IOException("GitHub did not confirm review thread resolution.");
        }
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
    /// <returns>The confirmed merge commit, or null when GitHub has not confirmed a merge.</returns>
    public async Task<string?> MergeAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        var merge = await ExecuteAsync(["pr", "merge", Number(pullRequest), "--repo", pullRequest.Repository, "--squash",
            "--match-head-commit", pullRequest.Head], cancellationToken);
        if (merge.ExitCode != 0) return null;
        var confirmed = await ExecuteAsync(["pr", "view", Number(pullRequest), "--repo", pullRequest.Repository,
            "--json", "state,mergeCommit"], cancellationToken);
        RequireSuccess(confirmed);
        using var document = JsonDocument.Parse(confirmed.Output);
        if (Text(document.RootElement, "state") != "MERGED" ||
            !document.RootElement.TryGetProperty("mergeCommit", out var commit) || commit.ValueKind != JsonValueKind.Object) return null;
        var head = Text(commit, "oid");
        return head.Length == 40 && head.All(char.IsAsciiHexDigit) ? head : null;
    }

    /// <summary>
    /// Reads all reported checks and overall workflow completion for the exact merged revision.
    /// </summary>
    /// <param name="repository">The repository name.</param>
    /// <param name="commit">The exact merge commit.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The combined check results.</returns>
    public async Task<IReadOnlyList<CheckSnapshot>> GetCommitChecksAsync(string repository, string commit,
        CancellationToken cancellationToken)
    {
        var revision = Uri.EscapeDataString(commit);
        var checks = new List<CheckSnapshot>();
        var jobs = await ReadPagesAsync($"repos/{repository}/commits/{revision}/check-runs?filter=latest&per_page=100",
            "check_runs", cancellationToken);
        checks.AddRange(jobs.Where(job => !job.TryGetProperty("app", out var app) || app.ValueKind != JsonValueKind.Object ||
            Text(app, "slug") != "dependabot").Select(job => new CheckSnapshot(Text(job, "name"),
            Text(job, "status") == "completed" ? Text(job, "conclusion").ToUpperInvariant() : Text(job, "status").ToUpperInvariant(),
            Text(job, "html_url"))));
        var statuses = await ReadPagesAsync($"repos/{repository}/commits/{revision}/status?per_page=100", "statuses", cancellationToken);
        checks.AddRange(statuses.Select(status => new CheckSnapshot(Text(status, "context"),
            Text(status, "state").ToUpperInvariant(), Text(status, "target_url"))));
        var runs = await ReadPagesAsync($"repos/{repository}/actions/runs?head_sha={revision}&per_page=100",
            "workflow_runs", cancellationToken);
        checks.AddRange(runs.Where(run => Text(run, "event") is "push" or "workflow_run")
            .GroupBy(run => run.GetProperty("workflow_id").GetInt64())
            .Select(group => group.MaxBy(run => run.GetProperty("id").GetInt64()))
            .Select(run => new CheckSnapshot("workflow: " + Text(run, "name"),
                Text(run, "status") == "completed" ? Text(run, "conclusion").ToUpperInvariant() : Text(run, "status").ToUpperInvariant(),
                Text(run, "html_url"))));
        return checks;
    }

    private async Task<JsonElement[]> ReadPagesAsync(string endpoint, string field, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(["api", endpoint, "--paginate", "--slurp"], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        return document.RootElement.EnumerateArray().SelectMany(page => page.GetProperty(field).EnumerateArray())
            .Select(value => value.Clone()).ToArray();
    }

    /// <summary>
    /// Resolves a follow-up base commit and independently verifies that it includes the original merge.
    /// </summary>
    /// <param name="repository">The repository name.</param>
    /// <param name="branch">The base branch.</param>
    /// <param name="ancestor">The original merge commit.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The confirmed descendant commit, or null.</returns>
    public async Task<string?> GetBranchDescendantAsync(string repository, string branch, string ancestor,
        CancellationToken cancellationToken)
    {
        var head = await GetBranchHeadAsync(repository, branch, cancellationToken);
        if (head == ancestor) return null;
        var comparison = await ExecuteAsync(["api", $"repos/{repository}/compare/{Uri.EscapeDataString(ancestor)}...{head}",
            "--jq", ".status"], cancellationToken);
        RequireSuccess(comparison);
        return comparison.Output.Trim() == "ahead" ? head : null;
    }

    /// <summary>
    /// Resolves a branch to a full GitHub commit identifier.
    /// </summary>
    /// <param name="repository">The selected repository.</param>
    /// <param name="branch">The branch to resolve.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The exact branch head.</returns>
    public async Task<string> GetBranchHeadAsync(string repository, string branch, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(["api", $"repos/{repository}/commits/{Uri.EscapeDataString(branch)}", "--jq", ".sha"],
            cancellationToken);
        RequireSuccess(result);
        var head = result.Output.Trim();
        if (head.Length != 40 || !head.All(char.IsAsciiHexDigit)) throw new IOException("GitHub returned an invalid branch head.");
        return head;
    }

    /// <summary>
    /// Reruns workflows associated with the inspected commit without changing repository files.
    /// </summary>
    /// <param name="repository">The selected repository.</param>
    /// <param name="commit">The exact revision to verify.</param>
    /// <param name="checks">The inspected checks.</param>
    /// <param name="failedOnly">Whether only failed jobs should be retried.</param>
    /// <param name="cancellationToken">Cancels requests.</param>
    /// <returns>Whether the requested workflow refreshes succeeded.</returns>
    public async Task<bool> RerunChecksAsync(string repository, string commit, IReadOnlyList<CheckSnapshot> checks, bool failedOnly,
        CancellationToken cancellationToken)
    {
        var runs = checks.Where(check => (!failedOnly || check.Failed) && Uri.TryCreate(check.Url, UriKind.Absolute, out var url) &&
            url.Scheme == "https" && url.Host == "github.com" &&
            url.AbsolutePath.StartsWith("/" + repository + "/actions/runs/", StringComparison.OrdinalIgnoreCase))
            .Select(check => RunUrl().Match(check.Url))
            .Where(match => match.Success).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var run in runs)
        {
            var metadata = await ExecuteAsync(["run", "view", run, "--repo", repository, "--json", "headSha", "--jq", ".headSha"],
                cancellationToken);
            if (metadata.ExitCode != 0 || metadata.Output.Trim() != commit) return false;
            string[] options = failedOnly ? ["--failed"] : [];
            var result = await ExecuteAsync(["run", "rerun", run, "--repo", repository, .. options], cancellationToken);
            if (result.ExitCode != 0) return false;
        }
        return runs.Length > 0;
    }

    /// <summary>
    /// Adopts only the authenticated account's PR on the exact recovery branch and base.
    /// </summary>
    /// <param name="request">The expected recovery identity.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>The existing managed recovery, or null.</returns>
    public async Task<PullRequestSnapshot?> FindRecoveryAsync(RecoveryRequest request, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(["pr", "list", "--repo", request.Repository, "--head", request.Branch,
            "--state", "all", "--limit", "100", "--json", Fields], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        var matches = document.RootElement.EnumerateArray().ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new IOException("Multiple PRs use the recovery branch; manual review is required.");
        if (_actor is null)
        {
            var actor = await ExecuteAsync(["api", "user", "--jq", ".login"], cancellationToken);
            RequireSuccess(actor);
            _actor = actor.Output.Trim();
            if (string.IsNullOrWhiteSpace(_actor)) throw new IOException("The authenticated GitHub account could not be identified.");
        }
        var pullRequest = Parse(request.Repository, matches[0]);
        if (pullRequest.Author != _actor || pullRequest.Branch != request.Branch ||
            pullRequest.BaseBranch != request.BaseBranch || pullRequest.CrossRepository)
            throw new IOException("The recovery PR has an unexpected author, branch, or base.");
        return pullRequest with { ManagedRecovery = true };
    }

    /// <summary>
    /// Publishes one concise, assigned, labeled recovery PR and resumes it on repeated calls.
    /// </summary>
    /// <param name="request">The validated recovery branch.</param>
    /// <param name="profile">The trusted assignment policy.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    /// <returns>The confirmed recovery PR.</returns>
    public async Task<PullRequestSnapshot> CreateRecoveryPullRequestAsync(RecoveryRequest request, RepositoryProfile profile,
        CancellationToken cancellationToken)
    {
        var existing = await FindRecoveryAsync(request, cancellationToken);
        if (existing is not null) return existing;
        var labels = await ExecuteAsync(["label", "list", "--repo", request.Repository, "--limit", "1000", "--json", "name"],
            cancellationToken);
        RequireSuccess(labels);
        using var document = JsonDocument.Parse(labels.Output);
        var label = document.RootElement.EnumerateArray().Select(value => Text(value, "name"))
            .FirstOrDefault(name => name.Equals("bug", StringComparison.OrdinalIgnoreCase));
        if (label is null)
        {
            RequireSuccess(await ExecuteAsync(["label", "create", "bug", "--repo", request.Repository,
                "--color", "d73a4a", "--description", "Something isn't working"], cancellationToken));
            label = "bug";
        }
        var body = $"Repair the CI failures after dependency update #{request.SourcePullRequest}. " +
            "The changes passed the configured local verification and Picket scan.";
        var result = await ExecuteAsync(["pr", "create", "--repo", request.Repository, "--base", request.BaseBranch,
            "--head", request.Branch, "--title", $"Fix CI after #{request.SourcePullRequest}", "--body", body,
            "--assignee", profile.RecoveryAssignee, "--label", label], cancellationToken);
        var created = await FindRecoveryAsync(request, cancellationToken);
        if (created is not null) return created;
        RequireSuccess(result);
        throw new IOException("The recovery PR could not be confirmed after creation.");
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
    /// Opens an issue for an actionable blocker or closes its managed issue after a verified merge.
    /// </summary>
    /// <param name="entry">The independently determined maintenance outcome.</param>
    /// <param name="model">The configured model ID.</param>
    /// <param name="destination">An optional explicit reporting repository override.</param>
    /// <param name="cancellationToken">Cancels publication.</param>
    internal async Task PublishAttentionAsync(ReportEntry entry, string model, string? destination, CancellationToken cancellationToken)
    {
        if (entry.Outcome is not ("blocked" or "merged")) return;
        var repository = destination ?? entry.Repository;
        var identity = entry.Repository + (entry.Number > 0 ? "#" + entry.Number : string.Empty);
        var title = "Depkeeper needs attention: " + identity;
        var marker = $"<!-- depkeeper:{entry.Repository}:{entry.Number} -->";
        var result = await ExecuteAsync(["issue", "list", "--repo", repository, "--state", "open", "--search",
            title + " in:title", "--json", "number,title,body", "--limit", "100"], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        var issue = document.RootElement.EnumerateArray().FirstOrDefault(value => Text(value, "title") == title &&
            Text(value, "body").Contains(marker, StringComparison.Ordinal));
        if (entry.Outcome == "merged")
        {
            if (issue.ValueKind != JsonValueKind.Undefined)
                RequireSuccess(await ExecuteAsync(["issue", "close", issue.GetProperty("number").GetRawText(),
                    "--repo", repository, "--reason", "completed"], cancellationToken));
            return;
        }
        var body = marker + "\n\n" + ReportWriter.Format([entry], model, false, _redactor);
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
                    Text(check, "detailsUrl", Text(check, "targetUrl")),
                    DateTimeOffset.TryParse(Text(check, "completedAt", Text(check, "createdAt")), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var completed) ? completed : null));
            }
        }
        return new PullRequestSnapshot(repository, value.GetProperty("number").GetInt32(), Text(value, "title"),
            value.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object
                ? Text(author, "login") : string.Empty,
            Text(value, "headRefName"), Text(value, "headRefOid"), Text(value, "baseRefName"),
            value.GetProperty("isDraft").GetBoolean(), value.GetProperty("isCrossRepository").GetBoolean(),
            Text(value, "mergeable"), Text(value, "mergeStateStatus"), Text(value, "reviewDecision"), checks,
            Text(value, "state", "OPEN"), value.TryGetProperty("mergeCommit", out var merged) &&
                merged.ValueKind == JsonValueKind.Object ? Text(merged, "oid") : null);
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
        var values = document.RootElement.EnumerateArray().ToArray();
        var changes = values.Select(value => new DependencyChange(Text(value, "change_type"),
            Text(value, "ecosystem"), Text(value, "name"), Text(value, "version"),
            value.GetProperty("vulnerabilities").EnumerateArray().Select(item => Text(item, "advisory_ghsa_id")).ToArray())).ToArray();
        var locks = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < changes.Length; index++)
        {
            var change = changes[index];
            var manifest = Text(values[index], "manifest");
            if (change.ChangeType != "added" || change.Ecosystem != "npm" || NpmLockResolver.IsExact(change.Version) ||
                Path.GetFileName(manifest) != "package.json") continue;
            var directory = manifest.Contains('/') ? manifest[..(manifest.LastIndexOf('/') + 1)] : string.Empty;
            while (true)
            {
                var lockPath = directory + "package-lock.json";
                if (!locks.TryGetValue(lockPath, out var content))
                {
                    content = await ReadRepositoryFileAsync(pullRequest, lockPath, cancellationToken);
                    locks[lockPath] = content;
                }
                if (content is not null)
                {
                    using var locked = JsonDocument.Parse(content);
                    var version = NpmLockResolver.Resolve(locked.RootElement, manifest[directory.Length..], change.Name, change.Version);
                    if (version is not null) changes[index] = change with { Version = version };
                    break;
                }
                if (directory.Length == 0) break;
                var parent = directory.TrimEnd('/');
                directory = parent.Contains('/') ? parent[..(parent.LastIndexOf('/') + 1)] : string.Empty;
            }
        }
        return changes;
    }

    /// <summary>
    /// Confirms that a managed recovery with an empty dependency diff contains only complete code-only patches.
    /// </summary>
    /// <param name="pullRequest">The verified controller-owned PR.</param>
    /// <param name="cancellationToken">Cancels retrieval.</param>
    /// <returns>Whether publication-age policy has no dependency versions to evaluate.</returns>
    internal async Task<bool> IsCodeOnlyRecoveryAsync(PullRequestSnapshot pullRequest, CancellationToken cancellationToken)
    {
        if (!pullRequest.ManagedRecovery) return false;
        var result = await ExecuteAsync(["api", $"repos/{pullRequest.Repository}/pulls/{Number(pullRequest)}/files?per_page=100",
            "--paginate", "--slurp"], cancellationToken);
        RequireSuccess(result);
        using var document = JsonDocument.Parse(result.Output);
        var files = document.RootElement.EnumerateArray().SelectMany(page => page.EnumerateArray()).ToArray();
        return files.Length is > 0 and <= 30 && files.All(DependencyEditPolicy.IsCodeOnly);
    }

    private async Task<string?> ReadRepositoryFileAsync(PullRequestSnapshot pullRequest, string path, CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(["api", $"repos/{pullRequest.Repository}/contents/{Uri.EscapeDataString(path)}" +
            "?ref=" + Uri.EscapeDataString(pullRequest.Head)], cancellationToken);
        if (result.ExitCode != 0) return null;
        using var document = JsonDocument.Parse(result.Output);
        if (Text(document.RootElement, "encoding") != "base64") return null;
        return Encoding.UTF8.GetString(Convert.FromBase64String(Text(document.RootElement, "content")));
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
        return document.RootElement.GetProperty("data").GetProperty("repository").GetProperty("releases")
            .GetProperty("nodes").EnumerateArray()
            .Where(release => release.TryGetProperty("tagCommit", out var tag) && tag.ValueKind == JsonValueKind.Object &&
                Text(tag, "oid") == commit.Output.Trim())
            .Select(release => release.GetProperty("publishedAt").ValueKind == JsonValueKind.String &&
                release.GetProperty("publishedAt").TryGetDateTimeOffset(out var published) ? published : (DateTimeOffset?)null)
            .Min();
    }

    private Task<CommandResult> ExecuteAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null) =>
        _execute is null
            ? ProcessRunner.RunAsync("gh", arguments, environment: _environment, input: input, cancellationToken: cancellationToken)
            : _execute(arguments, cancellationToken, input);

    private static string Text(JsonElement value, string name, string fallback = "") =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString()! : fallback;

    private static string Number(PullRequestSnapshot pullRequest) => pullRequest.Number.ToString(CultureInfo.InvariantCulture);

    private static (string Owner, string Name) SplitRepository(string repository)
    {
        var separator = repository.IndexOf('/');
        return (repository[..separator], repository[(separator + 1)..]);
    }

    private static void RequireSuccess(CommandResult result)
    {
        if (result.ExitCode != 0) throw new IOException("GitHub operation failed; check repository access and current branch rules.");
    }

    [GeneratedRegex(@"/actions/runs/(\d+)(?:/job/(\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex RunUrl();
}
