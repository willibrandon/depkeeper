using System.CommandLine;

namespace Depkeeper.Cli;

/// <summary>
/// Verifies live Copilot repair against a disposable fixture without changing GitHub repositories.
/// </summary>
internal static class RepairSmokeCommand
{
    /// <summary>
    /// Creates an explicit, opt-in live integration check.
    /// </summary>
    /// <param name="output">The result writer.</param>
    /// <param name="error">The diagnostic writer.</param>
    /// <returns>The live smoke command.</returns>
    internal static Command Create(TextWriter output, TextWriter error)
    {
        var command = new Command("repair-smoke", "Verify live Copilot tool use and repair on a disposable fixture.");
        var model = new Option<string>("--model")
        {
            DefaultValueFactory = _ => Environment.GetEnvironmentVariable("DEPKEEPER_MODEL") ?? "auto"
        };
        command.Options.Add(model);
        command.SetAction(async (result, cancellationToken) =>
        {
            var temporary = Directory.CreateTempSubdirectory("depkeeper-smoke-").FullName;
            var directory = Path.Join(temporary, "checkout");
            Directory.CreateDirectory(directory);
            var token = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN");
            try
            {
                if (string.IsNullOrWhiteSpace(token))
                {
                    var login = await ProcessRunner.RunAsync("gh", ["auth", "token", "--hostname", "github.com"],
                        cancellationToken: cancellationToken);
                    if (login.ExitCode != 0) throw new InvalidOperationException("Sign into gh before running the smoke check.");
                    token = login.Output.Trim();
                }
                var initialization = await ProcessRunner.RunAsync("git", ["init", directory], cancellationToken: cancellationToken);
                if (initialization.ExitCode != 0) throw new IOException("Could not initialize the smoke fixture.");
                const string test = "const t=require('node:test'),a=require('node:assert/strict'),sum=require('./sum.cjs');" +
                    "t('adds two numbers',()=>a.equal(sum(2,3),5));\n";
                await File.WriteAllTextAsync(Path.Join(directory, "sum.cjs"), "module.exports = (a, b) => a - b;\n",
                    cancellationToken);
                await File.WriteAllTextAsync(Path.Join(directory, "sum.test.cjs"), test, cancellationToken);
                var redactor = new Redactor(token);
                using var container = new ContainerRunner(directory, "node:24-bookworm", redactor);
                var before = await container.RunAsync("node --test", cancellationToken);
                if (before.ExitCode == 0) throw new InvalidOperationException("The smoke fixture unexpectedly passed before repair.");
                if (!before.Output.Contains("adds two numbers", StringComparison.Ordinal))
                    throw new InvalidOperationException("The validation container did not run the fixture: " + before.Error);
                var github = new GitHubGateway(token, redactor);
                var repairer = new CopilotRepairer(result.GetValue(model)!, token, token, redactor, github);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                var summary = await repairer.RepairWorkspaceAsync(directory,
                    "Fix sum.cjs. Do not modify sum.test.cjs. Test output:\n" + before.Output + before.Error, container, timeout.Token);
                var after = await container.RunAsync("node --test", cancellationToken);
                var unchanged = await File.ReadAllTextAsync(Path.Join(directory, "sum.test.cjs"), cancellationToken);
                if (after.ExitCode != 0 || unchanged != test)
                    throw new InvalidOperationException("Live repair did not pass independent verification. " + summary +
                        "\nValidation: " + after.Output + after.Error);
                await output.WriteLineAsync("Live repair passed: Copilot tool use, code repair, and independent verification.");
                return 0;
            }
            catch (Exception exception) when (FailurePolicy.CanReport(exception))
            {
                await error.WriteLineAsync(new Redactor(token).Clean(exception.Message));
                return 1;
            }
            finally { Directory.Delete(temporary, true); }
        });
        return command;
    }
}
