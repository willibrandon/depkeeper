namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies local login fallback and explicit automation authentication without network requests.
/// </summary>
[TestClass]
public sealed class CopilotAuthenticationTests
{
    /// <summary>
    /// Missing environment credentials preserve the SDK's signed-in-user authentication path.
    /// </summary>
    /// <param name="token">An absent or blank environment credential.</param>
    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void MissingTokenUsesSignedInUser(string? token)
    {
        var options = CopilotAuthentication.CreateOptions(token);
        Assert.IsNull(options.GitHubToken);
        Assert.IsTrue(options.UseLoggedInUser);
    }

    /// <summary>
    /// Explicit credentials disable fallback to another signed-in identity.
    /// </summary>
    [TestMethod]
    public void ExplicitTokenDisablesSignedInUserFallback()
    {
        var options = CopilotAuthentication.CreateOptions("example-credential", "copilot");
        Assert.AreEqual("example-credential", options.GitHubToken);
        Assert.IsFalse(options.UseLoggedInUser);
        Assert.IsNull(options.Connection);
    }

    /// <summary>
    /// Signed-in-user authentication can reuse the installed CLI's executable identity.
    /// </summary>
    [TestMethod]
    public void LocalLoginUsesTheInstalledCliWhenProvided()
    {
        var options = CopilotAuthentication.CreateOptions(null, "copilot");
        var connection = Assert.IsInstanceOfType<GitHub.Copilot.StdioRuntimeConnection>(options.Connection);
        Assert.AreEqual("copilot", connection.Path);
        Assert.IsTrue(options.UseLoggedInUser);
    }
}
