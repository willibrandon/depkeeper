using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Extracts exact Hex and Git revisions from trusted base and PR-head mix.lock contents.
/// </summary>
internal static partial class MixLockParser
{
    /// <summary>
    /// Compares locked entries without evaluating Elixir terms, and rejects content it cannot read completely.
    /// </summary>
    /// <param name="before">The lock file at the exact base commit.</param>
    /// <param name="after">The lock file at the exact PR head.</param>
    /// <returns>Added and removed locked dependency records.</returns>
    internal static IReadOnlyList<DependencyChange> Compare(string? before, string? after)
    {
        var oldEntries = Parse(before).ToHashSet();
        var newEntries = Parse(after).ToHashSet();
        return newEntries.Except(oldEntries).Select(entry => Change("added", entry))
            .Concat(oldEntries.Except(newEntries).Select(entry => Change("removed", entry))).ToArray();
    }

    /// <summary>
    /// Identifies Mix lock files at any depth, including umbrella and nested projects.
    /// </summary>
    /// <param name="path">The repository-relative path.</param>
    /// <returns>Whether the file is a Mix lock file.</returns>
    internal static bool IsLockFile(string path) => Path.GetFileName(path) == "mix.lock";

    private static (string Ecosystem, string Name, string Version, string? Checksum)[] Parse(string? content)
    {
        if (content is null) return [];
        var text = content.Trim();
        if (text.Length > 8 * 1024 * 1024 || !text.StartsWith("%{", StringComparison.Ordinal)) throw Unreadable();
        return Split(Inner(text[1..], '{', '}')).Select(Entry).ToArray();
    }

    private static (string Ecosystem, string Name, string Version, string? Checksum) Entry(string entry)
    {
        var key = KeyPrefix().Match(entry);
        if (!key.Success) throw Unreadable();
        var fields = Split(Inner(entry[key.Length..], '{', '}'));
        if (fields.Count < 3) throw Unreadable();
        if (fields[0] == ":git")
        {
            var url = Unquote(fields[1]);
            var revision = Unquote(fields[2]);
            if (!GitUrl().IsMatch(url) || !GitRevision().IsMatch(revision)) throw Unreadable();
            return ("git", url, revision, null);
        }
        if (fields[0] != ":hex") throw Unreadable();
        var package = fields[1].StartsWith(':') ? fields[1][1..].Trim('"') : string.Empty;
        var version = Unquote(fields[2]);
        // Hex reads an absent or nil repository as hexpm, and a nil outer checksum leaves the release unverifiable.
        var repository = Optional(fields, 6) ?? "hexpm";
        var checksum = Optional(fields, 7)?.ToLowerInvariant();
        if (!PackageName().IsMatch(package) || !PackageVersion().IsMatch(version) || !RepositoryName().IsMatch(repository) ||
            checksum is not null && !Checksum().IsMatch(checksum)) throw Unreadable();
        return ("hex", repository == "hexpm" ? package : repository + "/" + package, version, checksum);
    }

    private static string Inner(string value, char open, char close)
    {
        var text = value.Trim();
        if (text.Length < 2 || text[0] != open || text[^1] != close) throw Unreadable();
        return text[1..^1];
    }

    private static List<string> Split(string content)
    {
        var elements = new List<string>();
        var closers = new Stack<char>();
        var start = 0;
        for (var index = 0; index < content.Length; index++)
        {
            var character = content[index];
            if (character == '"')
            {
                var end = content.IndexOf('"', index + 1);
                if (end < 0 || content.AsSpan(index + 1, end - index - 1).Contains('\\')) throw Unreadable();
                index = end;
            }
            else if (character is '{' or '[') closers.Push(character == '{' ? '}' : ']');
            else if (character is '}' or ']')
            {
                if (closers.Count == 0 || closers.Pop() != character) throw Unreadable();
            }
            else if (character == '\\') throw Unreadable();
            else if (character == ',' && closers.Count == 0)
            {
                elements.Add(content[start..index].Trim());
                start = index + 1;
            }
        }
        if (closers.Count != 0) throw Unreadable();
        var last = content[start..].Trim();
        if (last.Length > 0) elements.Add(last);
        if (elements.Any(element => element.Length == 0)) throw Unreadable();
        return elements;
    }

    private static string? Optional(List<string> fields, int index) =>
        fields.Count <= index || fields[index] == "nil" ? null : Unquote(fields[index]);

    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"' || value.AsSpan(1, value.Length - 2).ContainsAny('"', '\\'))
            throw Unreadable();
        return value[1..^1];
    }

    private static DependencyChange Change(string type, (string Ecosystem, string Name, string Version, string? Checksum) entry) =>
        new(type, entry.Ecosystem, entry.Name, entry.Version, [], entry.Checksum);

    private static IOException Unreadable() =>
        new("A mix.lock file could not be read completely; its dependency changes require manual review.");

    [GeneratedRegex(@"\A""[A-Za-z0-9_]+"":\s*", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPrefix();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageName();

    [GeneratedRegex(@"\A[0-9][0-9A-Za-z.+-]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageVersion();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9_.:-]{0,127}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryName();

    [GeneratedRegex(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Checksum();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9@:/._~+-]{0,255}\z", RegexOptions.CultureInvariant)]
    private static partial Regex GitUrl();

    [GeneratedRegex(@"\A[a-f0-9]{40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex GitRevision();
}
