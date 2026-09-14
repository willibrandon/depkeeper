namespace Depkeeper.Cli.Tests;

/// <summary>
/// Enforces source conventions that the standard formatter does not validate.
/// </summary>
[TestClass]
public sealed class SourceConventionsTests
{
    /// <summary>
    /// Keeps every maintained C# source line within the configured width.
    /// </summary>
    [TestMethod]
    public void CSharpLinesDoNotExceed140Characters()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Depkeeper.slnx"))) root = root.Parent;
        Assert.IsNotNull(root);
        var violations = new List<string>();
        string[] folders = ["src", "tests", "scripts"];
        foreach (var folder in folders)
        {
            foreach (var file in Directory.EnumerateFiles(Path.Join(root.FullName, folder), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root.FullName, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj")) continue;
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    if (lines[index].Length > 140) violations.Add($"{relative}:{index + 1}: {lines[index].Length} characters");
                }
            }
        }
        Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
    }
}
