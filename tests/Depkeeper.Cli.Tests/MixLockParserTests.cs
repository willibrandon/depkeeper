namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies exact mix.lock comparison and its refusal to interpret content it cannot read completely.
/// </summary>
[TestClass]
public sealed class MixLockParserTests
{
    /// <summary>
    /// Reports direct and transitive updates with their locked checksums while ignoring unchanged entries.
    /// </summary>
    [TestMethod]
    public void ReportsDirectAndTransitiveUpdates()
    {
        var before = MixLockFixture.Lock(MixLockFixture.Hex("jason", "1.4.4", 'a'),
            MixLockFixture.Hex("mint", "1.10.0", 'b', MixLockFixture.Requirements), MixLockFixture.Hex("req", "0.7.3", 'c'));
        var after = MixLockFixture.Lock(MixLockFixture.Hex("jason", "1.4.4", 'a'),
            MixLockFixture.Hex("mint", "1.10.1", 'd', MixLockFixture.Requirements), MixLockFixture.Hex("req", "0.7.4", 'e'));
        var changes = MixLockParser.Compare(before, after);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Ecosystem == "hex" && change.Name == "mint" &&
            change.Version == "1.10.1" && change.Checksum == MixLockFixture.Checksum('d'), changes);
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Name == "req" && change.Version == "0.7.4" &&
            change.Checksum == MixLockFixture.Checksum('e'), changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Name == "mint" && change.Version == "1.10.0", changes);
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Name == "req" && change.Version == "0.7.3", changes);
        Assert.HasCount(4, changes);
    }

    /// <summary>
    /// Treats a changed checksum for an unchanged version as a new release that needs registry verification.
    /// </summary>
    [TestMethod]
    public void ReportsChangedChecksumForSameVersion()
    {
        var changes = MixLockParser.Compare(MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'a')),
            MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'b')));
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Checksum == MixLockFixture.Checksum('b'), changes);
        Assert.HasCount(2, changes);
    }

    /// <summary>
    /// Ignores requirement-list edits that leave the locked version and checksum unchanged.
    /// </summary>
    [TestMethod]
    public void IgnoresRequirementOnlyEdits()
    {
        var changes = MixLockParser.Compare(MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'a')),
            MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'a', MixLockFixture.Requirements)));
        Assert.IsEmpty(changes);
    }

    /// <summary>
    /// Handles newly created, deleted, absent, and empty lock files.
    /// </summary>
    [TestMethod]
    public void HandlesCreatedDeletedAndEmptyLocks()
    {
        var content = MixLockFixture.Lock(MixLockFixture.Hex("jason", "1.4.4", 'a'));
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Name == "jason", MixLockParser.Compare(null, content));
        Assert.ContainsSingle(change => change.ChangeType == "removed" && change.Name == "jason", MixLockParser.Compare(content, null));
        Assert.IsEmpty(MixLockParser.Compare(null, null));
        Assert.IsEmpty(MixLockParser.Compare("%{}", "%{}\n"));
    }

    /// <summary>
    /// Namespaces packages from private or self-hosted repositories so they are never looked up as public packages.
    /// </summary>
    [TestMethod]
    public void NamespacesPrivateRepositoryPackages()
    {
        var changes = MixLockParser.Compare(null,
            MixLockFixture.Lock(MixLockFixture.Hex("internal_tool", "2.0.0", 'a', repository: "hexpm:acme")));
        Assert.ContainsSingle(change => change.Ecosystem == "hex" && change.Name == "hexpm:acme/internal_tool", changes);
    }

    /// <summary>
    /// Uses the package atom rather than the application key when a dependency is published under another name.
    /// </summary>
    [TestMethod]
    public void UsesPackageNameRatherThanApplicationKey()
    {
        var entry = "  \"plug\": {:hex, :plug_fork, \"1.0.0\", \"" + MixLockFixture.Checksum('0') + "\", [:mix], [], \"hexpm\", \"" +
            MixLockFixture.Checksum('a') + "\"},\n";
        Assert.ContainsSingle(change => change.Name == "plug_fork", MixLockParser.Compare(null, MixLockFixture.Lock(entry)));
    }

    /// <summary>
    /// Leaves legacy entries without an outer checksum unverifiable instead of guessing at one.
    /// </summary>
    [TestMethod]
    public void LegacyEntriesCarryNoChecksum()
    {
        var legacy = "  \"poison\": {:hex, :poison, \"3.1.0\", \"" + MixLockFixture.Checksum('0') + "\", [:mix], [], \"hexpm\"},\n";
        var minimal = "  \"tiny\": {:hex, :tiny, \"1.0.0\"},\n";
        var changes = MixLockParser.Compare(null, MixLockFixture.Lock(legacy, minimal));
        Assert.HasCount(2, changes);
        Assert.IsTrue(changes.All(change => change.Ecosystem == "hex" && change.Checksum is null));
    }

    /// <summary>
    /// Reads nil as Hex does: the public repository when unnamed, and an unverifiable release when no checksum is locked.
    /// </summary>
    [TestMethod]
    public void ReadsNilRepositoryAndChecksumAsHexDoes()
    {
        var entry = "  \"mint\": {:hex, :mint, \"1.10.1\", \"" + MixLockFixture.Checksum('0') + "\", [:mix], [], nil, nil},\n";
        Assert.ContainsSingle(change => change.Ecosystem == "hex" && change.Name == "mint" && change.Checksum is null,
            MixLockParser.Compare(null, MixLockFixture.Lock(entry)));
    }

    /// <summary>
    /// Accepts additional tuple fields written by newer Hex clients.
    /// </summary>
    [TestMethod]
    public void AcceptsFieldsFromNewerHexClients()
    {
        var entry = "  \"mint\": {:hex, :mint, \"1.10.1\", \"" + MixLockFixture.Checksum('0') + "\", [:mix], [], \"hexpm\", \"" +
            MixLockFixture.Checksum('a') + "\", [future: true]},\n";
        Assert.ContainsSingle(change => change.Name == "mint" && change.Checksum == MixLockFixture.Checksum('a'),
            MixLockParser.Compare(null, MixLockFixture.Lock(entry)));
    }

    /// <summary>
    /// Reports Git revision changes under an ecosystem that has no publication metadata.
    /// </summary>
    [TestMethod]
    public void ReportsGitRevisionChanges()
    {
        const string url = "https://github.com/example/library.git";
        var changes = MixLockParser.Compare(MixLockFixture.Lock(MixLockFixture.Git("library", url, 'a')),
            MixLockFixture.Lock(MixLockFixture.Git("library", url, 'b')));
        Assert.ContainsSingle(change => change.ChangeType == "added" && change.Ecosystem == "git" && change.Name == url &&
            change.Version == new string('b', 40) && change.Checksum is null, changes);
        Assert.HasCount(2, changes);
    }

    /// <summary>
    /// Normalizes checksum case so an equivalent lock does not appear to change.
    /// </summary>
    [TestMethod]
    public void NormalizesChecksumCase()
    {
        var upper = MixLockFixture.Hex("mint", "1.10.1", 'a').Replace(MixLockFixture.Checksum('a'), MixLockFixture.Checksum('A'),
            StringComparison.Ordinal);
        Assert.IsEmpty(MixLockParser.Compare(MixLockFixture.Lock(MixLockFixture.Hex("mint", "1.10.1", 'a')),
            MixLockFixture.Lock(upper)));
    }

    /// <summary>
    /// Refuses partial interpretation, because an unreadable head would otherwise resemble harmless removals.
    /// </summary>
    /// <param name="content">The untrusted lock content.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("not a lock file")]
    [DataRow("[\"mint\": {:hex, :mint, \"1.0.0\"}]")]
    [DataRow("%{\n  \"mint\": {:hex, :mint, \"1.0.0\"},\n")]
    [DataRow("%{\n  \"mint\": {:hex, :mint, \"1.0.0\"]},\n}")]
    [DataRow("%{\n  \"mint\": {:hex, :mint, \"1.0.0},\n}")]
    [DataRow("%{\n  \"mint\": {:hex, :mint, \"1.0\\\"0\"},\n}")]
    [DataRow("%{\n<<<<<<< HEAD\n  \"mint\": {:hex, :mint, \"1.0.0\"},\n=======\n  \"mint\": {:hex, :mint, \"1.0.1\"},\n>>>>>>> pr\n}")]
    [DataRow("%{\n  \"mint\": {:hex, :mint, \"1.0.0\"},,\n}")]
    [DataRow("%{\n  \"mint\": {:hex, :mint},\n}")]
    [DataRow("%{\n  \"mint\": {:hex, \"mint\", \"1.0.0\"},\n}")]
    [DataRow("%{\n  \"mint\": {:hex, :mint, :latest},\n}")]
    [DataRow("%{\n  \"mint\": {:hex, :mint, \"../../admin\"},\n}")]
    [DataRow("%{\n  \"mint\": {:hex, :Mint, \"1.0.0\"},\n}")]
    [DataRow("%{\n  \"mint\": {:custom_scm, :mint, \"1.0.0\"},\n}")]
    [DataRow("%{\n  \"mint\": :hex,\n}")]
    [DataRow("%{\n  mint: {:hex, :mint, \"1.0.0\"},\n}")]
    [DataRow("%{\n  \"library\": {:git, \"https://example.com/x.git\", \"main\", []},\n}")]
    [DataRow("%{\n  \"library\": {:git, \"https://example.com/x y.git\", \"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\", []},\n}")]
    public void RejectsContentItCannotReadCompletely(string content)
    {
        Assert.Throws<IOException>(() => MixLockParser.Compare(null, content));
        Assert.Throws<IOException>(() => MixLockParser.Compare(content, null));
    }

    /// <summary>
    /// Rejects malformed checksums and repository names in otherwise complete entries.
    /// </summary>
    /// <param name="repository">The locked repository name.</param>
    /// <param name="checksum">The locked outer checksum.</param>
    [TestMethod]
    [DataRow("hexpm", "abc123")]
    [DataRow("hexpm", "zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [DataRow("hex pm", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [DataRow("../hexpm", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void RejectsMalformedChecksumsAndRepositories(string repository, string checksum)
    {
        var entry = "  \"mint\": {:hex, :mint, \"1.0.0\", \"" + MixLockFixture.Checksum('0') + "\", [:mix], [], \"" + repository +
            "\", \"" + checksum + "\"},\n";
        Assert.Throws<IOException>(() => MixLockParser.Compare(null, MixLockFixture.Lock(entry)));
    }

    /// <summary>
    /// Rejects oversized content before scanning it.
    /// </summary>
    [TestMethod]
    public void RejectsOversizedContent()
    {
        var content = "%{" + new string(' ', 8 * 1024 * 1024) + "}";
        Assert.Throws<IOException>(() => MixLockParser.Compare(null, content));
    }

    /// <summary>
    /// Recognizes lock files in root, umbrella, and nested projects without matching similarly named files.
    /// </summary>
    /// <param name="path">The repository-relative path.</param>
    /// <param name="expected">Whether the path is a Mix lock file.</param>
    [TestMethod]
    [DataRow("mix.lock", true)]
    [DataRow("apps/web/mix.lock", true)]
    [DataRow("mix.exs", false)]
    [DataRow("mix.lock.bak", false)]
    [DataRow("docs/mix.lock.md", false)]
    [DataRow("Mix.lock", false)]
    public void RecognizesLockFiles(string path, bool expected) => Assert.AreEqual(expected, MixLockParser.IsLockFile(path));
}
