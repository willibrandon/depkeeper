using System.Xml;
using System.Xml.Linq;

namespace Depkeeper.Cli;

/// <summary>
/// Extracts exact Maven dependency and plugin versions from trusted base and PR-head POM contents.
/// </summary>
internal static class MavenDependencyParser
{
    /// <summary>
    /// Compares exact Maven coordinates without resolving properties, inheritance, ranges, or repositories.
    /// </summary>
    /// <param name="before">The POM at the exact base commit.</param>
    /// <param name="after">The POM at the exact PR head.</param>
    /// <returns>Added and removed Maven package records.</returns>
    internal static IReadOnlyList<DependencyChange> Compare(string? before, string? after)
    {
        var oldVersions = Parse(before).ToHashSet();
        var newVersions = Parse(after).ToHashSet();
        return newVersions.Except(oldVersions).Select(value => Change("added", value))
            .Concat(oldVersions.Except(newVersions).Select(value => Change("removed", value))).ToArray();
    }

    private static IEnumerable<(string Name, string Version)> Parse(string? content)
    {
        if (content is null) return [];
        using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        var root = document.Root;
        if (root is null) return [];
        var ns = root.Name.Namespace;
        var dependencies = root.Descendants(ns + "dependency").Select(value => Coordinate(value, ns, null));
        var plugins = root.Descendants(ns + "plugin").Select(value => Coordinate(value, ns, "org.apache.maven.plugins"));
        return dependencies.Concat(plugins).OfType<(string Name, string Version)>().Distinct();
    }

    private static (string Name, string Version)? Coordinate(XElement element, XNamespace ns, string? defaultGroup)
    {
        var group = element.Element(ns + "groupId")?.Value.Trim() ?? defaultGroup;
        var artifact = element.Element(ns + "artifactId")?.Value.Trim();
        var version = element.Element(ns + "version")?.Value.Trim();
        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(artifact) || string.IsNullOrWhiteSpace(version) ||
            !version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')) return null;
        return (group + ":" + artifact, version);
    }

    private static DependencyChange Change(string type, (string Name, string Version) value) =>
        new(type, "maven", value.Name, value.Version, []);
}
