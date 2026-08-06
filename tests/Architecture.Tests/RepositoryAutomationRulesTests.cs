using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class RepositoryAutomationRulesTests
{
    private static readonly string[] ProductionTuple =
    [
        "path", "role", "deployableName", "databaseOwnerOverride", "migrationOwnerOverride",
    ];

    private static readonly string[] TestTuple = ["path", "testCategory"];
    private static readonly string[] ProjectRoots = ["src", "tests"];

    private static readonly HashSet<string> ScannedTextExtensions = new(
        [
            ".cs", ".csproj", ".json", ".md", ".props", ".ps1", ".py", ".sh",
            ".slnx", ".targets", ".yaml", ".yml",
        ],
        StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void RepositoryHasOneReadOnlyAutomaticWorkflow()
    {
        var repository = RepositoryModel.Load();
        var root = Path.Combine(repository.Root, ".github", "workflows");
        var workflows = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        Assert.True(
            workflows.Length == 1 &&
            repository.Relative(workflows[0]) == ".github/workflows/ci.yml",
            "The repository must have exactly one automatic workflow: .github/workflows/ci.yml.");

        var workflow = File.ReadAllText(workflows[0]);
        var forbidden = new[]
        {
            "contents: write", "git push", "git commit", "commit-tree",
            "createOrUpdateFileContents", "updateRef(",
        };
        Assert.DoesNotContain(
            forbidden,
            token => workflow.Contains(token, StringComparison.OrdinalIgnoreCase));

        var requiredOrder = new[]
        {
            "python3 .tools/complete-backend.py --check",
            "python3 tools/verify-contracts.py",
            "docker compose --env-file .env.example --file compose.yaml config --quiet",
            "dotnet test tests/Architecture.Tests/Architecture.Tests.csproj",
            "dotnet restore AggregatorBackend.Runtime.slnx",
            "dotnet build AggregatorBackend.Runtime.slnx",
            "dotnet restore AggregatorBackend.slnx",
            "dotnet build AggregatorBackend.slnx",
            "dotnet test AggregatorBackend.slnx",
        };
        var previous = -1;
        foreach (var command in requiredOrder)
        {
            var current = workflow.IndexOf(command, StringComparison.Ordinal);
            Assert.True(current > previous, $"CI command is missing or out of order: {command}");
            previous = current;
        }

        Assert.True(workflow.Contains("dotnet format whitespace", StringComparison.Ordinal));
        Assert.False(workflow.Contains("dotnet format AggregatorBackend.slnx", StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalSolutionsMatchApprovedProjectTopology()
    {
        var repository = RepositoryModel.Load();
        var topology = ReadTopology(repository);
        var approved = topology.Projects.Select(project => project.Path).Order(StringComparer.Ordinal).ToArray();
        var runtime = topology.Projects
            .Where(project => project.ProjectKind == "production")
            .Select(project => project.Path)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var physical = DiscoverProjects(repository.Root);

        Assert.True(
            approved.SequenceEqual(physical, StringComparer.Ordinal),
            BuildSetFailure("approved topology", approved, physical));
        Assert.All(
            topology.ForbiddenProjectPathPrefixes,
            prefix => Assert.DoesNotContain(physical, path => path.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.True(
            ReadSolutionProjects(repository.Root, "AggregatorBackend.slnx")
                .SequenceEqual(approved, StringComparer.Ordinal),
            "AggregatorBackend.slnx differs from the approved topology.");
        Assert.True(
            ReadSolutionProjects(repository.Root, "AggregatorBackend.Runtime.slnx")
                .SequenceEqual(runtime, StringComparer.Ordinal),
            "AggregatorBackend.Runtime.slnx differs from the approved production topology.");
        Assert.False(File.Exists(Path.Combine(repository.Root, "AggregatorBackend.All.slnx")));
    }

    [Fact]
    public void ObsoleteOwnerContoursCannotReturn()
    {
        var repository = RepositoryModel.Load();
        var topology = ReadTopology(repository);
        var manifest = Path.Combine(repository.Root, "docs", "architecture", "project-topology.json");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(repository.Root, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(path, manifest, StringComparison.OrdinalIgnoreCase) || !ShouldScan(repository, path))
            {
                continue;
            }

            var content = File.ReadAllText(path);
            foreach (var token in topology.ForbiddenReferenceTokens)
            {
                if (content.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add($"{repository.Relative(path)} contains '{token}'");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Obsolete owner contours or deployment references returned:\n" +
            string.Join('\n', violations.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void AcceptanceSupportRootsStayOwnerScopedAndLegacyRunnerIsAbsent()
    {
        var repository = RepositoryModel.Load();
        var root = Path.Combine(repository.Root, "tests", "Acceptance");
        Assert.Empty(References(repository, Path.Combine(root, "Acceptance.Contracts", "Acceptance.Contracts.csproj")));
        Assert.All(
            References(repository, Path.Combine(root, "Acceptance.Control", "Acceptance.Control.csproj")),
            target => Assert.True(
                target == "tests/Acceptance/Acceptance.Contracts/Acceptance.Contracts.csproj" ||
                target.StartsWith("src/Catalog/", StringComparison.Ordinal),
                $"Catalog acceptance control references non-Catalog owner '{target}'."));
        Assert.All(
            References(repository, Path.Combine(root, "Acceptance.Analytics.Control", "Acceptance.Analytics.Control.csproj")),
            target => Assert.True(
                target == "tests/Acceptance/Acceptance.Contracts/Acceptance.Contracts.csproj" ||
                target.StartsWith("src/Analytics/", StringComparison.Ordinal),
                $"Analytics acceptance control references non-Analytics owner '{target}'."));
        Assert.False(Directory.Exists(Path.Combine(root, "Acceptance.Runner")));
    }

    [Fact]
    public void TransientAgentDiagnosticsAreNotTracked()
    {
        var repository = RepositoryModel.Load();
        var tracked = new List<string>();
        foreach (var relative in new[] { ".codex/ci", ".codex/probes", ".codex/tmp" })
        {
            var path = Path.Combine(repository.Root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path))
            {
                tracked.AddRange(Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(repository.Relative));
            }
        }

        var patches = Path.Combine(repository.Root, ".codex", "patches");
        if (Directory.Exists(patches))
        {
            tracked.AddRange(Directory.EnumerateFiles(patches, "*.py", SearchOption.AllDirectories).Select(repository.Relative));
        }

        Assert.True(tracked.Count == 0, "Transient diagnostics must stay outside Git:\n" + string.Join('\n', tracked));
    }

    private static ProjectTopology ReadTopology(RepositoryModel repository)
    {
        var path = Path.Combine(repository.Root, "docs", "architecture", "project-topology.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(ProductionTuple, ReadStringArray(root, "productionTuple"));
        Assert.Equal(TestTuple, ReadStringArray(root, "testTuple"));

        var projects = new List<ProjectEntry>();
        foreach (var context in root.GetProperty("contexts").EnumerateArray())
        {
            Assert.Equal("active", RequiredString(context, "status"));
            foreach (var row in context.GetProperty("production").EnumerateArray())
            {
                Assert.Equal(5, row.GetArrayLength());
                projects.Add(new ProjectEntry(RequiredString(row[0]), "production"));
            }
            foreach (var row in context.GetProperty("tests").EnumerateArray())
            {
                Assert.Equal(2, row.GetArrayLength());
                projects.Add(new ProjectEntry(RequiredString(row[0]), "test"));
            }
        }

        Assert.NotEmpty(projects);
        Assert.Equal(projects.Count, projects.Select(project => project.Path).Distinct(StringComparer.Ordinal).Count());
        return new ProjectTopology(
            projects.OrderBy(project => project.Path, StringComparer.Ordinal).ToArray(),
            ReadStringArray(root, "forbiddenProjectPathPrefixes"),
            ReadStringArray(root, "forbiddenReferenceTokens"));
    }

    private static string RequiredString(JsonElement owner, string name) =>
        owner.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException($"Topology field '{name}' is empty.");

    private static string RequiredString(JsonElement value) =>
        value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException("Topology tuple value is empty.");

    private static string[] ReadStringArray(JsonElement owner, string name) =>
        owner.GetProperty(name).EnumerateArray().Select(RequiredString).ToArray();

    private static bool ShouldScan(RepositoryModel repository, string path)
    {
        var parts = repository.Relative(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !parts.Any(part => part is ".git" or ".idea" or ".vs" or "artifacts" or "bin" or "obj") &&
            ScannedTextExtensions.Contains(Path.GetExtension(path));
    }

    private static string[] DiscoverProjects(string root) =>
        ProjectRoots
            .SelectMany(owner => Directory.EnumerateFiles(Path.Combine(root, owner), "*.csproj", SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ReadSolutionProjects(string root, string fileName) =>
        XDocument.Load(Path.Combine(root, fileName))
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value ?? throw new InvalidDataException("Project Path is missing."))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] References(RepositoryModel repository, string projectPath)
    {
        Assert.True(File.Exists(projectPath), $"Required acceptance project '{repository.Relative(projectPath)}' is missing.");
        var directory = Path.GetDirectoryName(projectPath) ?? throw new InvalidDataException("Project directory is missing.");
        return XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? throw new InvalidDataException("ProjectReference Include is missing."))
            .Select(include => Path.GetFullPath(Path.Combine(directory, include)))
            .Select(repository.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildSetFailure(string owner, IReadOnlyCollection<string> expected, IReadOnlyCollection<string> actual) =>
        $"{owner} differs.\nMissing:\n{string.Join('\n', expected.Except(actual))}\nExtra:\n{string.Join('\n', actual.Except(expected))}";

    private sealed record ProjectTopology(
        IReadOnlyList<ProjectEntry> Projects,
        IReadOnlyList<string> ForbiddenProjectPathPrefixes,
        IReadOnlyList<string> ForbiddenReferenceTokens);

    private sealed record ProjectEntry(string Path, string ProjectKind);
}
