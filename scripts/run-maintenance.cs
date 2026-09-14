#!/usr/bin/env dotnet
#:include ../src/Depkeeper.Cli/ProcessRunner.cs
#:include ../src/Depkeeper.Cli/CommandResult.cs

using Depkeeper.Cli;

var arguments = new List<string>
{
    "run", "--project", "src/Depkeeper.Cli", "--configuration", "Release", "--no-build", "--", "run"
};
if (Environment.GetEnvironmentVariable("DRY_RUN") == "true") arguments.Add("--dry-run");
if (Environment.GetEnvironmentVariable("RETRY_BLOCKED") == "true") arguments.Add("--retry-blocked");
foreach (var (variable, option) in new (string, string)[]
{
    ("ONLY_REPOSITORY", "--repository"), ("ONLY_PR", "--pr"),
    ("DEPKEEPER_MIN_RELEASE_AGE_DAYS", "--minimum-release-age-days"),
    ("DEPKEEPER_MAX_REPAIRS", "--max-repairs")
})
{
    var value = Environment.GetEnvironmentVariable(variable);
    if (string.IsNullOrWhiteSpace(value)) continue;
    arguments.Add(option);
    arguments.Add(value);
}
var result = await ProcessRunner.RunAsync("dotnet", arguments);
Console.Write(result.Output);
Console.Error.Write(result.Error);
return result.ExitCode;
