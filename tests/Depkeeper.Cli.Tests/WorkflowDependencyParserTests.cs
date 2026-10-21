namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies exact action reference extraction from workflow and action metadata files.
/// </summary>
[TestClass]
public sealed class WorkflowDependencyParserTests
{
    /// <summary>
    /// Finds pinned, tagged, and reusable workflow references while ignoring local actions and container images.
    /// </summary>
    [TestMethod]
    public void ComparesRemoteActionReferences()
    {
        const string before = """
            jobs:
              build:
                uses: owner/shared/.github/workflows/build.yml@v1
              test:
                steps:
                  - uses: actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a # v7.0.1
                  - uses: ./.github/actions/local
                  - uses: docker://alpine:3.22
                  - name: Analyze
                    uses: 'github/codeql-action/analyze@v4'
            """;
        const string after = """
            jobs:
              build:
                uses: owner/shared/.github/workflows/build.yml@v1
              test:
                steps:
                  - uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7.0.2
                  - uses: actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9 # v7.0.2
                  - uses: ./.github/actions/local
                  - uses: docker://alpine:3.23
                  - name: Analyze
                    uses: "github/codeql-action/analyze@v5"
            """;
        var changes = WorkflowDependencyParser.Compare(before, after);
        Assert.HasCount(4, changes);
        Assert.IsTrue(changes.All(change => change.Ecosystem == "actions"));
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Name == "actions/upload-artifact" &&
            change.Version == "cf430e030ddbb5b0abf93d22962f4752f3646cd9", changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Name == "actions/upload-artifact" &&
            change.Version == "043fb46d1a93c77aae656e7c1c64a875d1fc6a0a", changes);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Name == "github/codeql-action/analyze" &&
            change.Version == "v5", changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Version == "v4", changes);
    }

    /// <summary>
    /// Treats a removed workflow as removing its references.
    /// </summary>
    [TestMethod]
    public void RemovedWorkflowRemovesReferences()
    {
        var change = WorkflowDependencyParser.Compare("    - uses: actions/checkout@v7\n", null).Single();
        Assert.AreEqual("removed", change.ChangeType);
        Assert.AreEqual("actions/checkout", change.Name);
    }

    /// <summary>
    /// Recognizes top-level workflows and action metadata but not nested or unrelated YAML files.
    /// </summary>
    /// <param name="path">The repository-relative path.</param>
    /// <param name="expected">Whether the file defines action dependencies.</param>
    [TestMethod]
    [DataRow(".github/workflows/ci.yml", true)]
    [DataRow(".github/workflows/release.yaml", true)]
    [DataRow(".github/actions/setup/action.yml", true)]
    [DataRow("action.yaml", true)]
    [DataRow(".github/workflows/templates/ci.yml", false)]
    [DataRow(".github/dependabot.yml", false)]
    [DataRow("docs/workflow.md", false)]
    public void RestrictsWorkflowPaths(string path, bool expected)
    {
        Assert.AreEqual(expected, WorkflowDependencyParser.IsWorkflow(path));
    }
}
