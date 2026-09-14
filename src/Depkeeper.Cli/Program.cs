using Depkeeper.Cli;

return await Commands.RunAsync(args, Console.Out, Console.Error,
    () => Environment.GetEnvironmentVariable("COPILOT_GITHUB_TOKEN"));
