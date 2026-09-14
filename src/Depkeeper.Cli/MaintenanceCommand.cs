using System.CommandLine;

namespace Depkeeper.Cli;

/// <summary>
/// Connects command-line options to the maintenance controller and reporting pipeline.
/// </summary>
internal static class MaintenanceCommand
{
    /// <summary>
    /// Creates the maintenance command using injectable output streams.
    /// </summary>
    /// <param name="output">The result output writer.</param>
    /// <param name="error">The diagnostic writer.</param>
    /// <returns>The configured command.</returns>
    internal static Command Create(TextWriter output, TextWriter error)
    {
        var command = new Command("run", "Inspect Dependabot PRs, repair CI failures, and merge verified updates.");
        var config = new Option<string?>("--config") { Description = "Deployment JSON file (default: depkeeper.json if present)." };
        var repositories = new Option<string[]>("--repository") { AllowMultipleArgumentsPerToken = true };
        var model = new Option<string?>("--model");
        var dryRun = new Option<bool>("--dry-run") { Description = "Inspect only; no model calls or GitHub mutations." };
        var retry = new Option<bool>("--retry-blocked") { Description = "Explicitly retry blocked revisions." };
        var repairs = new Option<int>("--max-repairs") { DefaultValueFactory = _ => 3 };
        var attempts = new Option<int>("--max-attempts") { DefaultValueFactory = _ => 2 };
        var repairMinutes = new Option<int>("--repair-minutes") { DefaultValueFactory = _ => 30 };
        var ciMinutes = new Option<int>("--ci-minutes") { DefaultValueFactory = _ => 15 };
        var pr = new Option<int?>("--pr") { Description = "Inspect one PR in one selected repository." };
        var releaseAge = new Option<int?>("--minimum-release-age-days") { Description = "Publication cooldown (default: 3 days)." };
        var unknownAge = new Option<bool>("--allow-unknown-age") { Description = "Explicitly allow missing publication metadata." };
        var securityAge = new Option<bool>("--wait-for-security-fixes") { Description = "Apply cooldown to security fixes too." };
        var state = new Option<string>("--state") { DefaultValueFactory = _ => ".state/state.json" };
        var report = new Option<string>("--report") { DefaultValueFactory = _ => ".state/report.md" };
        var reportRepository = new Option<string?>("--report-repo")
        {
            Description = "Optional repository for actionable-blocker issues (default: each affected repository)."
        };
        Option[] options = [config, repositories, model, dryRun, retry, repairs, attempts,
            repairMinutes, ciMinutes, pr, state, report, reportRepository, releaseAge, unknownAge, securityAge];
        foreach (var option in options) command.Options.Add(option);
        command.SetAction(async (result, cancellationToken) =>
        {
            try
            {
                var settings = RunSettings.Load(result.GetValue(config), result.GetValue(model), result.GetValue(repositories) ?? [],
                    result.GetValue(dryRun), result.GetValue(retry), result.GetValue(repairs), result.GetValue(attempts),
                    result.GetValue(repairMinutes), result.GetValue(ciMinutes), result.GetValue(pr),
                    result.GetValue(releaseAge), result.GetValue(unknownAge), result.GetValue(securityAge));
                var token = Environment.GetEnvironmentVariable("GH_MAINTENANCE_TOKEN");
                if (string.IsNullOrWhiteSpace(token))
                {
                    var login = await ProcessRunner.RunAsync("gh", ["auth", "token", "--hostname", "github.com"],
                        cancellationToken: cancellationToken);
                    if (login.ExitCode != 0) throw new InvalidOperationException("Sign into gh or run the authentication setup app.");
                    token = login.Output.Trim();
                }
                var copilotToken = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN") ?? token;
                var redactor = new Redactor(token, copilotToken);
                var gateway = new GitHubGateway(token, redactor);
                var store = new StateStore(result.GetValue(state)!);
                var repairer = new CopilotRepairer(settings.Model, copilotToken, token, redactor, gateway);
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 8 * 1024 * 1024 };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Depkeeper/0.1");
                var docker = new DockerHubPublicationClient(http);
                var mcr = new McrPublicationClient(http);
                var pypi = new PyPiPublicationClient(http);
                var publications = new PublicationClient(http, gateway.GetActionPublicationAsync, DockerPublicationAsync,
                    pypi.GetAsync);
                var ageGate = new ReleaseAgeGate(gateway.GetDependencyChangesAsync, publications.GetAsync,
                    codeOnly: gateway.IsCodeOnlyRecoveryAsync);
                var controller = new MaintenanceRunner(gateway, repairer, store, redactor, ageGate);
                var entries = await controller.RunAsync(settings, cancellationToken);
                var markdown = ReportWriter.Format(entries, settings.Model, settings.DryRun, redactor);
                var reportPath = Path.GetFullPath(result.GetValue(report)!);
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                await File.WriteAllTextAsync(reportPath, markdown, cancellationToken);
                var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
                if (!string.IsNullOrWhiteSpace(summaryPath)) await File.AppendAllTextAsync(summaryPath, markdown, cancellationToken);
                var destination = result.GetValue(reportRepository);
                if (!settings.DryRun)
                {
                    if (destination is not null && !RunSettings.IsRepository(destination))
                        throw new InvalidDataException("Report repository must use OWNER/REPO format.");
                    foreach (var entry in entries)
                        await gateway.PublishAttentionAsync(entry, settings.Model, destination, cancellationToken);
                }
                await output.WriteLineAsync(string.Join("; ", entries.GroupBy(entry => entry.Outcome)
                    .Select(group => $"{group.Key}: {group.Count()}")));
                await output.WriteLineAsync($"Report: {reportPath}");
                return entries.Any(entry => entry.Outcome == "blocked") ? 2 : 0;

                async Task<DateTimeOffset?> DockerPublicationAsync(DependencyChange dependency, CancellationToken token) =>
                    await docker.GetAsync(dependency, token) ?? await mcr.GetAsync(dependency, token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception) when (FailurePolicy.CanReport(exception))
            {
                var redactor = new Redactor(Environment.GetEnvironmentVariable("GH_MAINTENANCE_TOKEN"),
                    Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN"));
                await error.WriteLineAsync(redactor.Clean(exception.Message));
                return 1;
            }
        });
        return command;
    }
}
