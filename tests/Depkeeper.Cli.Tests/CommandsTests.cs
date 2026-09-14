using System.Globalization;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies command boundaries without using live Copilot credentials.
/// </summary>
[TestClass]
public sealed class CommandsTests
{
    /// <summary>
    /// Informational commands complete without accessing authentication.
    /// </summary>
    /// <param name="command">The informational command to execute.</param>
    /// <returns>A task representing the test execution.</returns>
    [TestMethod]
    [DataRow("--help")]
    [DataRow("-h")]
    [DataRow("--version")]
    public async Task InformationalCommandsDoNotReadCredentials(string command)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await Commands.RunAsync([command], output, error,
            () => throw new InvalidOperationException("Credentials must not be accessed."));

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(string.IsNullOrWhiteSpace(output.ToString()));
        Assert.AreEqual(string.Empty, error.ToString());
    }

    /// <summary>
    /// Unknown commands fail as usage errors without reading credentials.
    /// </summary>
    /// <returns>A task representing the test execution.</returns>
    [TestMethod]
    public async Task UnknownCommandsReturnUsageErrorWithoutReadingCredentials()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);

        var exitCode = await Commands.RunAsync(["unknown"], output, error,
            () => throw new InvalidOperationException("Credentials must not be accessed."));

        Assert.AreEqual(2, exitCode);
        StringAssert.Contains(error.ToString(), "unknown");
    }
}
