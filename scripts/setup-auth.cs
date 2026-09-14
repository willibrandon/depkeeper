#!/usr/bin/env dotnet
#:package GitHub.Copilot.SDK
#:package System.CommandLine

using System.CommandLine;
using System.Diagnostics;
using GitHub.Copilot;

var repositoryOption = new Option<string?>("--repo") { Description = "Deployment repository (defaults to the current GitHub repository)." };
var command = new RootCommand("Configure Depkeeper authentication using your GitHub CLI login.") { repositoryOption };
command.SetAction(async (result, cancellationToken) =>
{
    try
    {
        var (statusExitCode, _) = await RunGitHubAsync(["auth", "status", "--hostname", "github.com"], cancellationToken);
        if (statusExitCode != 0)
        {
            var (loginExitCode, _) = await RunGitHubAsync(
                ["auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web", "--scopes", "repo,workflow"],
                cancellationToken, interactive: true);
            if (loginExitCode != 0) throw new InvalidOperationException();
        }

        var repository = result.GetValue(repositoryOption);
        if (string.IsNullOrWhiteSpace(repository))
        {
            var (repositoryExitCode, repositoryName) = await RunGitHubAsync(
                ["repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner"], cancellationToken);
            if (repositoryExitCode != 0) throw new InvalidOperationException();
            repository = repositoryName.Trim();
        }

        var segments = repository.Split('/');
        if (segments.Length != 2 || segments.Any(segment => segment.Length == 0 ||
            segment.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_')))
        {
            Console.Error.WriteLine("Use --repo OWNER/REPO.");
            return 2;
        }

        var (credentialExitCode, credentialOutput) = await RunGitHubAsync(["auth", "token", "--hostname", "github.com"], cancellationToken);
        if (credentialExitCode != 0 || string.IsNullOrWhiteSpace(credentialOutput)) throw new InvalidOperationException();
        var token = credentialOutput.Trim();

        Console.WriteLine("Checking Copilot access...");
        await using (var client = new CopilotClient(new CopilotClientOptions { GitHubToken = token, UseLoggedInUser = false }))
        {
            await client.StartAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            var models = await client.ListModelsAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (models.Count == 0) throw new InvalidOperationException();
        }

        foreach (var name in (string[])["COPILOT_GITHUB_TOKEN", "GH_MAINTENANCE_TOKEN"])
        {
            var (configuredExitCode, _) = await RunGitHubAsync(
                ["secret", "set", name, "--repo", repository], cancellationToken, input: token);
            if (configuredExitCode != 0)
            {
                Console.Error.WriteLine($"Could not configure {name} for {repository}.");
                return 1;
            }
        }

        Console.WriteLine($"Authentication configured for {repository}.");
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        return 130;
    }
    catch (Exception)
    {
        Console.Error.WriteLine("Setup failed. Verify that gh is installed and your GitHub login has Copilot and repository access.");
        return 1;
    }
});
return await command.Parse(args).InvokeAsync();

static async Task<(int ExitCode, string Output)> RunGitHubAsync(
    IReadOnlyList<string> arguments, CancellationToken cancellationToken, string? input = null, bool interactive = false)
{
    var start = new ProcessStartInfo("gh")
    {
        UseShellExecute = false,
        RedirectStandardOutput = !interactive,
        RedirectStandardError = !interactive,
        RedirectStandardInput = input is not null
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException();
    var output = interactive ? Task.FromResult(string.Empty) : process.StandardOutput.ReadToEndAsync(cancellationToken);
    var error = interactive ? Task.FromResult(string.Empty) : process.StandardError.ReadToEndAsync(cancellationToken);
    try
    {
        if (input is not null)
        {
            await process.StandardInput.WriteLineAsync(input.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        await process.WaitForExitAsync(cancellationToken);
        await error;
        return (process.ExitCode, await output);
    }
    catch
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        throw;
    }
}
