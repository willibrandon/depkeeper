namespace Depkeeper.Cli.Tests;

/// <summary>
/// Builds synthetic mix.lock content in the single-line entry layout that Mix writes.
/// </summary>
internal static class MixLockFixture
{
    /// <summary>
    /// Gets a requirement list whose nested tuples, keyword lists, and quoted commas exercise top-level splitting.
    /// </summary>
    internal const string Requirements = "[{:castore, \"~> 0.1.0 or ~> 1.0\", [hex: :castore, repo: \"hexpm\", optional: true]}, " +
        "{:hpax, \"~> 0.1.1 or ~> 0.2.0 or ~> 1.0\", [hex: :hpax, repo: \"hexpm\", optional: false]}]";

    /// <summary>
    /// Creates a lock file from complete entry lines.
    /// </summary>
    /// <param name="entries">The entry lines, each ending with Mix's trailing comma.</param>
    /// <returns>The lock file content.</returns>
    internal static string Lock(params string[] entries) => "%{\n" + string.Concat(entries) + "}\n";

    /// <summary>
    /// Creates a current-format Hex entry with distinct inner and outer checksums.
    /// </summary>
    /// <param name="name">The application and package name.</param>
    /// <param name="version">The locked version.</param>
    /// <param name="checksum">The character repeated to form the outer checksum.</param>
    /// <param name="requirements">The locked requirement list.</param>
    /// <param name="repository">The Hex repository name.</param>
    /// <returns>One lock entry line.</returns>
    internal static string Hex(string name, string version, char checksum, string requirements = "[]",
        string repository = "hexpm") =>
        $"  \"{name}\": {{:hex, :{name}, \"{version}\", \"{new string('0', 64)}\", [:mix], {requirements}, " +
        $"\"{repository}\", \"{Checksum(checksum)}\"}},\n";

    /// <summary>
    /// Creates a Git entry locked to an exact revision.
    /// </summary>
    /// <param name="name">The application name.</param>
    /// <param name="url">The remote URL.</param>
    /// <param name="revision">The character repeated to form the commit identifier.</param>
    /// <returns>One lock entry line.</returns>
    internal static string Git(string name, string url, char revision) =>
        $"  \"{name}\": {{:git, \"{url}\", \"{new string(revision, 40)}\", [branch: \"main\"]}},\n";

    /// <summary>
    /// Creates the outer checksum used by a synthetic Hex entry.
    /// </summary>
    /// <param name="checksum">The repeated hexadecimal character.</param>
    /// <returns>A 64-character checksum.</returns>
    internal static string Checksum(char checksum) => new(checksum, 64);
}
