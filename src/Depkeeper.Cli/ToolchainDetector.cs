using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depkeeper.Cli;

/// <summary>
/// Selects standard package-manager workflows without tying maintenance to a programming language.
/// </summary>
internal static partial class ToolchainDetector
{
    /// <summary>
    /// Detects a toolchain and applies explicit deployment overrides.
    /// </summary>
    /// <param name="directory">The repository root.</param>
    /// <param name="profile">Trusted deployment overrides.</param>
    /// <returns>The validation toolchain.</returns>
    internal static ToolchainProfile Resolve(string directory, RepositoryProfile profile)
    {
        if (profile.Image != "auto" && profile.Install is not null && profile.Verify is not null)
            return new ToolchainProfile("custom", profile.Image, profile.Install, profile.Verify);
        var detected = Detect(directory);
        if (detected is null && (profile.Install is null || profile.Verify is null || profile.Image == "auto"))
            throw new InvalidOperationException("Configure an image, install commands, and verify commands for this toolchain.");
        return new ToolchainProfile(detected?.Name ?? "custom", profile.Image == "auto" ? detected!.Image : profile.Image,
            profile.Install ?? detected!.Install, profile.Verify ?? detected!.Verify);
    }

    private static ToolchainProfile? Detect(string directory)
    {
        bool Has(string file) => File.Exists(Path.Join(directory, file));
        if (Has("package.json")) return Node(directory);
        var solutions = Directory.GetFiles(directory, "*.sln*").Where(path => path.EndsWith(".sln", StringComparison.Ordinal) ||
            path.EndsWith(".slnx", StringComparison.Ordinal)).ToArray();
        var projects = Directory.GetFiles(directory, "*.*proj")
            .Where(path => Path.GetExtension(path) is ".csproj" or ".fsproj" or ".vbproj");
        var dotnet = solutions.Length > 0 ? solutions : projects.ToArray();
        if (dotnet.Length > 0)
        {
            var image = "mcr.microsoft.com/dotnet/sdk:10.0";
            var mtp = false;
            if (Has("global.json"))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, "global.json")),
                    new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                mtp = json.RootElement.TryGetProperty("test", out var test) && test.TryGetProperty("runner", out var runner) &&
                    runner.GetString() == "Microsoft.Testing.Platform";
                if (json.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version))
                {
                    var value = version.GetString();
                    if (value is not null && value.All(character => char.IsAsciiDigit(character) || character == '.')) image =
                        "mcr.microsoft.com/dotnet/sdk:" + value;
                }
            }
            var restore = dotnet.Select(path => "dotnet restore " + Quote(Path.GetFileName(path))).ToArray();
            var verify = dotnet.SelectMany(path => new[]
            {
                "dotnet build " + Quote(Path.GetFileName(path)) + " --configuration Release --no-restore",
                "dotnet test " + (mtp ? solutions.Length > 0 ? "--solution " : "--project " : string.Empty) +
                    Quote(Path.GetFileName(path)) + " --configuration Release --no-restore"
            }).ToArray();
            return new ToolchainProfile("dotnet", image, restore, verify);
        }
        if (Has("Cargo.toml")) return new ToolchainProfile("rust", "rust:latest", ["cargo fetch"], ["cargo test --all-targets"]);
        if (Has("go.mod")) return new ToolchainProfile("go", "golang:latest", ["go mod download"], ["go test ./...", "go vet ./..."]);
        if (Has("pyproject.toml") || Has("requirements.txt"))
        {
            var install = new List<string> { "python -m venv /cache/venv", "/cache/venv/bin/pip install pytest" };
            if (Has("requirements.txt")) install.Add("/cache/venv/bin/pip install -r requirements.txt");
            if (Has("requirements-dev.txt")) install.Add("/cache/venv/bin/pip install -r requirements-dev.txt");
            if (Has("pyproject.toml")) install.Add("/cache/venv/bin/pip install -e '.[test]'");
            return new ToolchainProfile("python", "python:3", install.ToArray(), ["/cache/venv/bin/python -m pytest"]);
        }
        if (Has("pom.xml")) return new ToolchainProfile("maven", "maven:latest",
            ["mvn --batch-mode --no-transfer-progress -Dmaven.repo.local=/cache/maven dependency:go-offline"],
            ["mvn --batch-mode --no-transfer-progress -Dmaven.repo.local=/cache/maven verify"]);
        if (Has("gradlew")) return new ToolchainProfile("gradle", "gradle:latest", ["sh ./gradlew --no-daemon dependencies"],
            ["sh ./gradlew --no-daemon check"]);
        if (Has("Package.swift")) return new ToolchainProfile("swift", "swift:latest", ["swift package resolve"], ["swift test"]);
        if (Has("composer.json")) return new ToolchainProfile("php", "composer:latest", ["composer install --no-interaction"],
            ["composer test --no-interaction"]);
        if (Has("Gemfile")) return new ToolchainProfile("ruby", "ruby:latest", ["bundle install"], ["bundle exec rake test"]);
        return null;
    }

    private static ToolchainProfile Node(string directory)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, "package.json")));
        if (!json.RootElement.TryGetProperty("scripts", out var scripts))
            throw new InvalidOperationException("Configure verification commands for this Node project.");
        var manager = File.Exists(Path.Join(directory, "pnpm-lock.yaml")) ? "pnpm" :
            File.Exists(Path.Join(directory, "yarn.lock")) ? "yarn" : "npm";
        var image = "node:24-trixie";
        if (json.RootElement.TryGetProperty("engines", out var engines) && engines.TryGetProperty("node", out var node) &&
            node.ValueKind == JsonValueKind.String && ExactVersion().IsMatch(node.GetString()!))
            image = "node:" + node.GetString() + "-trixie";
        var setup = new List<string>();
        if (json.RootElement.TryGetProperty("packageManager", out var packageManager) &&
            packageManager.ValueKind == JsonValueKind.String)
        {
            var declared = packageManager.GetString()!.Split('@');
            if (declared.Length == 2 && declared[0] == "npm" && ExactVersion().IsMatch(declared[1]))
                setup.Add("npm install --prefix /cache/toolchain --ignore-scripts npm@" + declared[1]);
        }
        var install = manager switch
        {
            "pnpm" => "corepack pnpm install --frozen-lockfile",
            "yarn" => "corepack yarn install --immutable",
            _ => File.Exists(Path.Join(directory, "package-lock.json")) ? "npm ci" : "npm install"
        };
        var run = manager == "npm" ? "npm run " : "corepack " + manager + " run ";
        var verify = scripts.TryGetProperty("verify", out _) ? [run + "verify"] :
            scripts.TryGetProperty("check", out _) ? [run + "check"] :
            new[] { "check:generated", "format:check", "lint", "typecheck",
                scripts.TryGetProperty("test:coverage", out _) ? "test:coverage" : "test", "build", "audit" }
                .Where(name => scripts.TryGetProperty(name, out _)).Select(name => run + name).ToArray();
        if (verify.Length == 0) throw new InvalidOperationException("No verification commands were found for this Node project.");
        if (scripts.TryGetProperty("check:licenses", out _))
            verify = verify.Append(run + "check:licenses").Distinct(StringComparer.Ordinal).ToArray();
        setup.Add(install);
        return new ToolchainProfile("node", image, setup.ToArray(), verify);
    }

    [GeneratedRegex(@"\A\d+\.\d+\.\d+\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExactVersion();

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
