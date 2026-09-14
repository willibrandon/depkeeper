namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies conservative extraction and normalization of fully pinned Dockerfile references.
/// </summary>
[TestClass]
public sealed class DockerDependencyParserTests
{
    /// <summary>
    /// Finds official Docker Hub digest updates while ignoring stage names and unpinned images.
    /// </summary>
    [TestMethod]
    public void ParsesPinnedDockerHubUpdates()
    {
        var oldDigest = new string('a', 64);
        var newDigest = new string('b', 64);
        var before = $"FROM docker:29.7.2-cli@sha256:{oldDigest} AS tool\nFROM tool\n";
        var after = $"FROM --platform=linux/amd64 docker:29.7.2-cli@sha256:{newDigest} AS tool\nFROM tool\n";
        var changes = DockerDependencyParser.Compare(before, after);
        Assert.HasCount(2, changes);
        var added = changes.Single(change => change.ChangeType == "added");
        Assert.AreEqual("docker", added.Ecosystem);
        Assert.AreEqual("docker.io/library/docker", added.Name);
        Assert.AreEqual("29.7.2-cli@sha256:" + newDigest, added.Version);
    }

    /// <summary>
    /// Recognizes Dockerfile variants but not unrelated files with similar names.
    /// </summary>
    [TestMethod]
    public void RestrictsDockerfilePaths()
    {
        Assert.IsTrue(DockerDependencyParser.IsDockerfile(".devcontainer/Dockerfile"));
        Assert.IsTrue(DockerDependencyParser.IsDockerfile("build/Containerfile.release"));
        Assert.IsFalse(DockerDependencyParser.IsDockerfile("docs/Dockerfile.md"));
        Assert.IsFalse(DockerDependencyParser.IsDockerfile("src/docker.ts"));
    }
}
