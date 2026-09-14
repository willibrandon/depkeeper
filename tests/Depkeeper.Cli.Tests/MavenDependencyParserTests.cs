using System.Xml;

namespace Depkeeper.Cli.Tests;

/// <summary>
/// Verifies conservative extraction of exact Maven dependency and plugin versions.
/// </summary>
[TestClass]
public sealed class MavenDependencyParserTests
{
    /// <summary>
    /// Finds exact plugin and dependency updates while applying Maven's default plugin group.
    /// </summary>
    [TestMethod]
    public void ParsesExactMavenUpdates()
    {
        const string before = """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <dependencies>
                <dependency><groupId>example</groupId><artifactId>library</artifactId><version>1.0.0</version></dependency>
              </dependencies>
              <build><plugins><plugin><artifactId>maven-compiler-plugin</artifactId><version>3.15.0</version></plugin></plugins></build>
            </project>
            """;
        const string after = """
            <project xmlns="http://maven.apache.org/POM/4.0.0">
              <dependencies>
                <dependency><groupId>example</groupId><artifactId>library</artifactId><version>1.1.0</version></dependency>
              </dependencies>
              <build><plugins><plugin><artifactId>maven-compiler-plugin</artifactId><version>3.16.0</version></plugin></plugins></build>
            </project>
            """;
        var changes = MavenDependencyParser.Compare(before, after);
        Assert.ContainsSingle(change => change.ChangeType == "added" &&
            change.Name == "org.apache.maven.plugins:maven-compiler-plugin" && change.Version == "3.16.0", changes);
        Assert.ContainsSingle(change => change.ChangeType == "added" &&
            change.Name == "example:library" && change.Version == "1.1.0", changes);
        Assert.HasCount(4, changes);
    }

    /// <summary>
    /// Does not claim publication metadata for inherited, property-based, or range versions.
    /// </summary>
    [TestMethod]
    public void SkipsVersionsThatNeedMavenResolution()
    {
        const string pom = """
            <project><dependencies>
              <dependency><groupId>example</groupId><artifactId>property</artifactId><version>${version}</version></dependency>
              <dependency><groupId>example</groupId><artifactId>range</artifactId><version>[1,2)</version></dependency>
              <dependency><artifactId>inherited</artifactId><version>1.0.0</version></dependency>
            </dependencies></project>
            """;
        Assert.IsEmpty(MavenDependencyParser.Compare(null, pom));
    }

    /// <summary>
    /// Rejects document type declarations instead of resolving external XML entities.
    /// </summary>
    [TestMethod]
    public void RejectsDocumentTypes()
    {
        const string pom = "<!DOCTYPE project [<!ENTITY value SYSTEM 'file:///etc/passwd'>]><project>&value;</project>";
        Assert.Throws<XmlException>(() => MavenDependencyParser.Compare(null, pom));
    }
}
