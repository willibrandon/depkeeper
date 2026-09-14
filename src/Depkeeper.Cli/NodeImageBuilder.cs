using System.Security.Cryptography;
using System.Text;

namespace Depkeeper.Cli;

/// <summary>
/// Adds native build prerequisites to the detected official Node image using controller-owned build instructions.
/// </summary>
internal static class NodeImageBuilder
{
    /// <summary>
    /// Builds a reusable validation image without running repository code or forwarding host credentials.
    /// </summary>
    /// <param name="baseImage">The controller-selected official Node image.</param>
    /// <param name="cancellationToken">Cancels the image build.</param>
    /// <returns>The local image tag containing native build tools.</returns>
    internal static async Task<string> BuildAsync(string baseImage, CancellationToken cancellationToken)
    {
        var dockerfile = "FROM " + baseImage + "\n" +
            "RUN apt-get update && apt-get install -y --no-install-recommends cmake ninja-build pkg-config " +
            "&& rm -rf /var/lib/apt/lists/*\n";
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dockerfile)));
        var image = "depkeeper-node:" + digest[..24];
        var existing = await ProcessRunner.RunAsync("docker", ["image", "inspect", image], cancellationToken: cancellationToken);
        if (existing.ExitCode == 0) return image;
        var directory = Directory.CreateTempSubdirectory("depkeeper-image-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "Dockerfile"), dockerfile, cancellationToken);
            var result = await ProcessRunner.RunAsync("docker", ["build", "--tag", image, directory],
                cancellationToken: cancellationToken);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("Could not prepare the Node validation image with native build prerequisites.");
            return image;
        }
        finally { Directory.Delete(directory, true); }
    }
}
