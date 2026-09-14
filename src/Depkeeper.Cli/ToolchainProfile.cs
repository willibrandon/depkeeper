namespace Depkeeper.Cli;

/// <summary>
/// Describes trusted installation and verification commands for a detected project toolchain.
/// </summary>
/// <param name="Name">The detected toolchain family.</param>
/// <param name="Image">The isolated Linux validation image.</param>
/// <param name="Install">Dependency preparation commands.</param>
/// <param name="Verify">Independent verification commands.</param>
internal sealed record ToolchainProfile(string Name, string Image, string[] Install, string[] Verify);
