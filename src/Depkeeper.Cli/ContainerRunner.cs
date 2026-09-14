namespace Depkeeper.Cli;

/// <summary>
/// Runs repository-controlled commands in credential-free Linux containers.
/// </summary>
internal sealed class ContainerRunner : IDisposable
{
    private readonly string _directory;
    private readonly string _image;
    private readonly Redactor _redactor;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Creates a runner for one checkout and validation image.
    /// </summary>
    /// <param name="directory">The checkout mounted into the container.</param>
    /// <param name="image">The trusted Docker image.</param>
    /// <param name="redactor">The diagnostic redactor.</param>
    internal ContainerRunner(string directory, string image, Redactor redactor)
    {
        _directory = directory;
        _image = image;
        _redactor = redactor;
    }

    /// <summary>
    /// Runs a shell command with read-only Git metadata and no host credentials.
    /// </summary>
    /// <param name="command">The command to execute inside the container.</param>
    /// <param name="cancellationToken">Cancels execution and removes the container.</param>
    /// <returns>The sanitized result.</returns>
    internal async Task<CommandResult> RunAsync(string command, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        var name = "depkeeper-" + Guid.NewGuid().ToString("N");
        try
        {
            var uid = await ProcessRunner.RunAsync("id", ["-u"], cancellationToken: cancellationToken);
            var gid = await ProcessRunner.RunAsync("id", ["-g"], cancellationToken: cancellationToken);
            if (uid.ExitCode != 0 || gid.ExitCode != 0) throw new IOException("Repairs require a Linux or macOS Docker host.");
            var cache = Path.Combine(Path.GetDirectoryName(_directory)!, "cache");
            Directory.CreateDirectory(cache);
            var arguments = new List<string>
            {
                "run", "--rm", "--name", name, "--cap-drop=ALL", "--security-opt=no-new-privileges",
                "--cpus=2", "--memory=4g", "--pids-limit=256", "--user", $"{uid.Output.Trim()}:{gid.Output.Trim()}",
                "--workdir", "/workspace", "--env", "HOME=/cache", "--env", "CI=true",
                "--env", "CARGO_HOME=/cache/cargo", "--env", "GOPATH=/cache/go", "--env", "GOCACHE=/cache/go-build",
                "--env", "GRADLE_USER_HOME=/cache/gradle", "--env", "BUNDLE_PATH=/cache/bundle",
                "--env", "NUGET_PACKAGES=/cache/nuget", "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
                "--mount", $"type=bind,source={cache},target=/cache",
                "--mount", $"type=bind,source={_directory},target=/workspace",
                "--mount", $"type=bind,source={Path.Combine(_directory, ".git")},target=/workspace/.git,readonly",
                _image, "bash", "-lc", "export PATH=/cache/toolchain/node_modules/.bin:$PATH; " + command
            };
            var result = await ProcessRunner.RunAsync("docker", arguments, cancellationToken: cancellationToken);
            return result with { Output = _redactor.Clean(result.Output), Error = _redactor.Clean(result.Error) };
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await ProcessRunner.RunAsync("docker", ["rm", "--force", name], cancellationToken: cleanup.Token); }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or
                System.ComponentModel.Win32Exception)
            { }
            _gate.Release();
        }
    }

    /// <summary>
    /// Releases the execution semaphore.
    /// </summary>
    public void Dispose() => _gate.Dispose();
}
