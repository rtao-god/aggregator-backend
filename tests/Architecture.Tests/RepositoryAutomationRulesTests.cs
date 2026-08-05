using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class RepositoryAutomationRulesTests
{
    [Fact]
    public void RepositoryHasOneReadOnlyAutomaticWorkflow()
    {
        var repository = RepositoryModel.Load();
        var workflowRoot = Path.Combine(repository.Root, ".github", "workflows");
        var workflows = Directory.Exists(workflowRoot)
            ? Directory
                .EnumerateFiles(workflowRoot, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path =>
                    string.Equals(Path.GetExtension(path), ".yml", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetExtension(path), ".yaml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];

        Assert.True(
            workflows.Length == 1 &&
            string.Equals(
                repository.Relative(workflows[0]),
                ".github/workflows/ci.yml",
                StringComparison.Ordinal),
            "The repository must have exactly one automatic workflow: .github/workflows/ci.yml.");

        var workflow = File.ReadAllText(workflows[0]);
        var forbidden = new[]
        {
            "contents: write",
            "git push",
            "git commit",
            "commit-tree",
            "createOrUpdateFileContents",
            "updateRef(",
        };
        var violations = forbidden
            .Where(token => workflow.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            "CI must be read-only and must never commit repository state: " +
            string.Join(", ", violations));

        var inventoryIndex = workflow.IndexOf(
            "python3 .tools/complete-backend.py --check",
            StringComparison.Ordinal);
        var architectureIndex = workflow.IndexOf(
            "dotnet test tests/Architecture.Tests/Architecture.Tests.csproj",
            StringComparison.Ordinal);
        var runtimeRestoreIndex = workflow.IndexOf(
            "dotnet restore AggregatorBackend.Runtime.slnx",
            StringComparison.Ordinal);
        var runtimeBuildIndex = workflow.IndexOf(
            "dotnet build AggregatorBackend.Runtime.slnx",
            StringComparison.Ordinal);
        var solutionRestoreIndex = workflow.IndexOf(
            "dotnet restore AggregatorBackend.slnx",
            StringComparison.Ordinal);
        var buildIndex = workflow.IndexOf(
            "dotnet build AggregatorBackend.slnx",
            StringComparison.Ordinal);
        var testIndex = workflow.IndexOf(
            "dotnet test AggregatorBackend.slnx",
            StringComparison.Ordinal);

        Assert.True(
            inventoryIndex >= 0 &&
            architectureIndex > inventoryIndex &&
            runtimeRestoreIndex > architectureIndex &&
            runtimeBuildIndex > runtimeRestoreIndex &&
            solutionRestoreIndex > runtimeBuildIndex &&
            buildIndex > solutionRestoreIndex &&
            testIndex > buildIndex,
            "CI must fail fast in this order: inventory, architecture, runtime restore/build, complete restore/build, tests.");

        Assert.True(
            workflow.Contains("dotnet format whitespace", StringComparison.Ordinal) &&
            !workflow.Contains(
                "dotnet format AggregatorBackend.slnx",
                StringComparison.Ordinal),
            "Automatic CI must use the bounded whitespace gate; full semantic format belongs to an explicit final command.");
    }

    [Fact]
    public void CanonicalSolutionsContainExactPhysicalProjectSets()
    {
        var repository = RepositoryModel.Load();
        var allProjects = DiscoverProjects(repository.Root, "src", "tests");
        var runtimeProjects = DiscoverProjects(repository.Root, "src");

        var canonical = ReadSolutionProjects(
            repository.Root,
            "AggregatorBackend.slnx");
        var runtime = ReadSolutionProjects(
            repository.Root,
            "AggregatorBackend.Runtime.slnx");

        Assert.True(
            canonical.SequenceEqual(allProjects, StringComparer.Ordinal),
            BuildSetFailure("AggregatorBackend.slnx", allProjects, canonical));
        Assert.True(
            runtime.SequenceEqual(runtimeProjects, StringComparer.Ordinal),
            BuildSetFailure("AggregatorBackend.Runtime.slnx", runtimeProjects, runtime));
        Assert.False(
            File.Exists(Path.Combine(repository.Root, "AggregatorBackend.All.slnx")),
            "AggregatorBackend.All.slnx is a stale second full-solution owner and must not exist.");
    }

    [Fact]
    public void AcceptanceCompositionRootsStayOwnerScoped()
    {
        var repository = RepositoryModel.Load();
        var acceptanceRoot = Path.Combine(repository.Root, "tests", "Acceptance");
        var contracts = References(
            repository,
            Path.Combine(acceptanceRoot, "Acceptance.Contracts", "Acceptance.Contracts.csproj"));
        var catalogControl = References(
            repository,
            Path.Combine(acceptanceRoot, "Acceptance.Control", "Acceptance.Control.csproj"));
        var analyticsControl = References(
            repository,
            Path.Combine(acceptanceRoot, "Acceptance.Analytics.Control", "Acceptance.Analytics.Control.csproj"));
        var runner = References(
            repository,
            Path.Combine(acceptanceRoot, "Acceptance.Runner", "Acceptance.Runner.csproj"));

        Assert.Empty(contracts);
        Assert.All(
            catalogControl,
            target => Assert.True(
                target == "tests/Acceptance/Acceptance.Contracts/Acceptance.Contracts.csproj" ||
                target.StartsWith("src/Catalog/", StringComparison.Ordinal),
                $"Catalog acceptance control references non-Catalog owner '{target}'."));
        Assert.All(
            analyticsControl,
            target => Assert.True(
                target == "tests/Acceptance/Acceptance.Contracts/Acceptance.Contracts.csproj" ||
                target.StartsWith("src/Analytics/", StringComparison.Ordinal),
                $"Analytics acceptance control references non-Analytics owner '{target}'."));
        Assert.All(
            runner,
            target => Assert.True(
                target == "tests/Acceptance/Acceptance.Contracts/Acceptance.Contracts.csproj" ||
                target.EndsWith(".Contracts.csproj", StringComparison.Ordinal),
                $"Acceptance runner may consume only shared test transport and producer Contracts, not '{target}'."));
    }

    [Fact]
    public void TransientAgentDiagnosticsAreNotTracked()
    {
        var repository = RepositoryModel.Load();
        var forbiddenDirectories = new[]
        {
            ".codex/ci",
            ".codex/probes",
            ".codex/tmp",
        };
        var trackedArtifacts = new List<string>();

        foreach (var relative in forbiddenDirectories)
        {
            var path = Path.Combine(
                repository.Root,
                relative.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path))
            {
                trackedArtifacts.AddRange(
                    Directory
                        .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                        .Select(repository.Relative));
            }
        }

        var patches = Path.Combine(repository.Root, ".codex", "patches");
        if (Directory.Exists(patches))
        {
            trackedArtifacts.AddRange(
                Directory
                    .EnumerateFiles(patches, "*.py", SearchOption.AllDirectories)
                    .Select(repository.Relative));
        }

        Assert.True(
            trackedArtifacts.Count == 0,
            "Transient diagnostics and repair scripts must stay outside Git:\n" +
            string.Join('\n', trackedArtifacts.Order(StringComparer.Ordinal)));
    }

    private static string[] DiscoverProjects(string root, params string[] owners) =>
        owners
            .SelectMany(owner =>
                Directory.EnumerateFiles(
                    Path.Combine(root, owner),
                    "*.csproj",
                    SearchOption.AllDirectories))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string[] ReadSolutionProjects(string root, string fileName)
    {
        var path = Path.Combine(root, fileName);
        Assert.True(File.Exists(path), $"Required solution '{fileName}' does not exist.");

        return XDocument
            .Load(path)
            .Descendants("Project")
            .Select(element =>
                element.Attribute("Path")?.Value ??
                throw new InvalidDataException(
                    $"Solution '{fileName}' contains a Project without Path."))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] References(RepositoryModel repository, string projectPath)
    {
        Assert.True(File.Exists(projectPath), $"Required acceptance project '{repository.Relative(projectPath)}' does not exist.");
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidDataException($"Project '{projectPath}' has no directory.");
        return XDocument
            .Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element =>
                element.Attribute("Include")?.Value ??
                throw new InvalidDataException(
                    $"Project '{repository.Relative(projectPath)}' contains a ProjectReference without Include."))
            .Select(include => Path.GetFullPath(Path.Combine(projectDirectory, include)))
            .Select(repository.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static string BuildSetFailure(
        string owner,
        IReadOnlyCollection<string> expected,
        IReadOnlyCollection<string> actual)
    {
        var missing = expected.Except(actual, StringComparer.Ordinal);
        var extra = actual.Except(expected, StringComparer.Ordinal);
        return $"{owner} does not match physical projects.\n" +
            $"Missing:\n{string.Join('\n', missing)}\n" +
            $"Extra:\n{string.Join('\n', extra)}";
    }
}
