using GitHub.Copilot;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine("""
        Depkeeper

        Commands:
          models       List models available through your Copilot subscription.
          --version    Print the application version.

        Set COPILOT_GITHUB_TOKEN before using the models command.
        Maintenance orchestration is the next implementation stage.
        """);
    return 0;
}

if (args is ["--version"])
{
    Console.WriteLine("0.1.0");
    return 0;
}

if (args is not ["models"])
{
    Console.Error.WriteLine("Unknown command. Use --help for available commands.");
    return 2;
}

string? token = Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Set COPILOT_GITHUB_TOKEN to a token with Copilot Requests access.");
    return 1;
}

try
{
    await using CopilotClient client = new(new CopilotClientOptions
    {
        GitHubToken = token,
        UseLoggedInUser = false
    });
    await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(30));
    var models = await client.ListModelsAsync().WaitAsync(TimeSpan.FromSeconds(30));
    foreach (var model in models)
    {
        Console.WriteLine(model.Id);
    }
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine("Could not load Copilot models. Check token permissions and Copilot availability.");
    return 1;
}
