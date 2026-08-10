namespace Architecture.Tests;

public sealed class CatalogUniversalityReachabilityTests
{
    private static readonly string[] TextExtensions =
    [
        ".cs",
        ".csproj",
        ".json",
        ".sql",
        ".yaml",
        ".yml",
    ];

    [Fact]
    public void SecondVerticalFixtureUsesTheExistingConfigurationOwnerPath()
    {
        var root = FindRepositoryRoot();
        var applicationProof = Read(
            root,
            "tests/Catalog/Catalog.Application.Tests/SecondCatalogProductConfigurationTests.cs");
        var persistenceProof = Read(
            root,
            "tests/Catalog/Catalog.Infrastructure.Tests/SecondCatalogProductConfigurationPersistenceTests.cs");

        Assert.Contains(
            "CatalogProductConfigurationSourceLoader",
            applicationProof,
            StringComparison.Ordinal);
        Assert.Contains(
            "CatalogProductConfigurationSourceLoader",
            persistenceProof,
            StringComparison.Ordinal);
        Assert.Contains("ImportAsync", persistenceProof, StringComparison.Ordinal);
        Assert.Contains("ActivateAsync", persistenceProof, StringComparison.Ordinal);
        Assert.DoesNotContain("CoworkingCatalogService", applicationProof, StringComparison.Ordinal);
        Assert.DoesNotContain("CoworkingCatalogService", persistenceProof, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondVerticalFixtureIsContrastingProductData()
    {
        var root = FindRepositoryRoot();
        var fixturePath = Path.Combine(
            root,
            "test-fixtures",
            "product-config",
            "berlin-coworking-spaces");
        Assert.True(
            Directory.Exists(fixturePath),
            "The second vertical product-config fixture is missing.");

        var contents = string.Join(
            "\n",
            Directory
                .EnumerateFiles(fixturePath, "*", SearchOption.AllDirectories)
                .Where(IsTextFile)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        Assert.Contains("berlin-coworking-spaces", contents, StringComparison.Ordinal);
        Assert.Contains("coworking-space", contents, StringComparison.Ordinal);
        Assert.Contains("meeting-room", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("recording-studio", contents, StringComparison.Ordinal);
    }

    [Fact]
    public void ReusableProductionCoreContainsNoSecondVerticalLiterals()
    {
        var root = FindRepositoryRoot();
        var productionRoot = Path.Combine(root, "src");
        var forbidden = new[]
        {
            "berlin-coworking-spaces",
            "coworking-space",
            "meeting-room",
            "CoworkingCatalog",
            "CoworkingSpace",
        };
        var violations = new List<string>();

        foreach (var path in Directory
                     .EnumerateFiles(productionRoot, "*", SearchOption.AllDirectories)
                     .Where(IsTextFile)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path);
            foreach (var literal in forbidden)
            {
                if (text.Contains(literal, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add(
                        $"{Path.GetRelativePath(root, path)} contains '{literal}'.");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Second-vertical identities leaked into reusable production core:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void SecondVerticalAddsNoBusinessDeployableOrDatabaseOwner()
    {
        var root = FindRepositoryRoot();
        var projectPaths = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path))
            .ToArray();

        Assert.DoesNotContain(
            projectPaths,
            path => path.Contains("cowork", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            projectPaths,
            path => path.Contains("meeting-room", StringComparison.OrdinalIgnoreCase));

        var compose = Read(root, "compose.yaml");
        Assert.DoesNotContain("coworking-api", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coworking-worker", compose, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("coworking_db", compose, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTextFile(string path) =>
        TextExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase);

    private static string Read(string root, string relativePath)
    {
        var path = Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Repository file '{relativePath}' was not found.");
        return File.ReadAllText(path);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
