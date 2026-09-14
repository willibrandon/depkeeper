#!/usr/bin/env dotnet
#:include ../src/Depkeeper.Cli/ProcessRunner.cs
#:include ../src/Depkeeper.Cli/CommandResult.cs

using System.Text.Json;
using Depkeeper.Cli;

var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
var branch = Environment.GetEnvironmentVariable("GITHUB_REF_NAME");
if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(branch))
{
    Console.Error.WriteLine("This utility runs inside the maintenance workflow.");
    return 1;
}

Directory.CreateDirectory(".state");
var response = await ProcessRunner.RunAsync("gh",
    ["api", $"repos/{repository}/actions/artifacts?name=depkeeper-state&per_page=100", "--paginate", "--slurp"]);
if (response.ExitCode != 0)
{
    Console.Error.WriteLine("Could not list maintenance checkpoints.");
    return 1;
}
using var document = JsonDocument.Parse(response.Output);
foreach (var run in document.RootElement.EnumerateArray()
    .SelectMany(page => page.GetProperty("artifacts").EnumerateArray())
    .Where(artifact => !artifact.GetProperty("expired").GetBoolean())
    .OrderByDescending(artifact => artifact.GetProperty("created_at").GetDateTimeOffset())
    .Select(artifact => artifact.GetProperty("workflow_run"))
    .Where(run => run.GetProperty("head_branch").GetString() == branch))
{
    var id = run.GetProperty("id").GetRawText();
    var metadata = await ProcessRunner.RunAsync("gh",
        ["run", "view", id, "--repo", repository, "--json", "workflowName", "--jq", ".workflowName"]);
    if (metadata.ExitCode != 0 || metadata.Output.Trim() != "Maintenance") continue;
    var downloaded = await ProcessRunner.RunAsync("gh",
        ["run", "download", id, "--repo", repository, "--name", "depkeeper-state", "--dir", ".state"]);
    if (downloaded.ExitCode != 0)
    {
        Console.Error.WriteLine("The latest checkpoint could not be downloaded; refusing to reset repair history.");
        return 1;
    }
    Console.WriteLine("Restored the latest maintenance checkpoint.");
    return 0;
}
Console.WriteLine("No retained checkpoint; starting a new maintenance history.");
return 0;
