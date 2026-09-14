namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies mapping from dependency-review action versions to Git refs.
/// </summary>
[TestClass]
public sealed class ActionPublicationTests
{
    /// <summary>
    /// Resolves floating major and minor forms while preserving named refs and commit identifiers.
    /// </summary>
    /// <param name="version">The dependency-review version.</param>
    /// <param name="expected">The first ref to query.</param>
    [TestMethod]
    [DataRow("5.*.*", "v5")]
    [DataRow("5.1.*", "v5.1")]
    [DataRow("5.1.2", "v5.1.2")]
    [DataRow("v5", "v5")]
    [DataRow("main", "main")]
    public void MapsDependencyVersionsToActionRefs(string version, string expected)
    {
        Assert.AreEqual(expected, GitHubGateway.ActionReferences(version)[0]);
    }
}
