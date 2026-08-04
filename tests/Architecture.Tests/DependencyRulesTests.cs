using System.Text.RegularExpressions;
using Xunit;

namespace Architecture.Tests;

public sealed partial class DependencyRulesTests
{
    private static readonly string[] ContextNames = ["Catalog", "Query", "Ingestion", "Analytics", "Promotion"];

    [Fact]
    public void EveryProjectReferenceTargetsAnExistingProject()
    {
        var repository = RepositoryModel.Load();
        var missing = repository.References
            .Where(edge => !File.Exists(edge.Target))
            .Select(edge => $"{repository.Relative(edge.Source)} -> {repository.Relative(edge.Target)}")
            .ToArray();

        Assert.True(missing.Length == 0, "Missing project references:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void DomainProjectsHaveNoProjectReferences()
    {
        var repository = RepositoryModel.Load();
        var invalid = repository.References
            .Where(edge => ProjectLayer(edge.Source) == "Domain")
            .Select(edge => $"{repository.Relative(edge.Source)} -> {repository.Relative(edge.Target)}")
            .ToArray();

        Assert.True(invalid.Length == 0, "Domain projects must be dependency-free:\n" + string.Join('\n', invalid));
    }

    [Fact]
    public void CrossContextReferencesTargetOnlyProducerContracts()
    {
        var repository = RepositoryModel.Load();
        var invalid = repository.References
            .Where(edge =>
            {
                var sourceContext = Context(edge.Source);
                var targetContext = Context(edge.Target);
                return sourceContext is not null
                    && targetContext is not null
                    && !string.Equals(sourceContext, targetContext, StringComparison.Ordinal)
                    && ProjectLayer(edge.Target) != "Contracts";
            })
            .Select(edge => $"{repository.Relative(edge.Source)} -> {repository.Relative(edge.Target)}")
            .ToArray();

        Assert.True(invalid.Length == 0, "Cross-context references may target only producer Contracts:\n" + string.Join('\n', invalid));
    }

    [Fact]
    public void BuildingBlocksDoNotReferenceBusinessContexts()
    {
        var repository = RepositoryModel.Load();
        var invalid = repository.References
            .Where(edge => IsBuildingBlock(edge.Source) && Context(edge.Target) is not null)
            .Select(edge => $"{repository.Relative(edge.Source)} -> {repository.Relative(edge.Target)}")
            .ToArray();

        Assert.True(invalid.Length == 0, "BuildingBlocks must not reference business contexts:\n" + string.Join('\n', invalid));
    }

    [Fact]
    public void BuildingBlocksDoNotDeclareBusinessOwnerTypes()
    {
        var repository = RepositoryModel.Load();
        var sourceRoot = Path.Combine(repository.Root, "src", "BuildingBlocks");
        var forbiddenNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Site", "Catalog", "Organization", "Place", "Provider", "Listing", "Claim",
            "Publication", "ImportBatch", "Promotion", "InteractionEvent",
        };
        var invalid = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var content = File.ReadAllText(file);
            foreach (Match match in TypeDeclarationRegex().Matches(content))
            {
                if (forbiddenNames.Contains(match.Groups[1].Value))
                {
                    invalid.Add($"{repository.Relative(file)} declares {match.Groups[1].Value}");
                }
            }
        }

        Assert.True(invalid.Count == 0, "Business owner types are forbidden in BuildingBlocks:\n" + string.Join('\n', invalid));
    }

    [Fact]
    public void GenericDomainUtilityFilesDoNotExist()
    {
        var repository = RepositoryModel.Load();
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Utils.cs", "Helpers.cs", "CommonService.cs", "BaseRepository.cs", "BaseEntity.cs",
        };
        var invalid = Directory
            .EnumerateFiles(Path.Combine(repository.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => forbidden.Contains(Path.GetFileName(path)))
            .Select(repository.Relative)
            .ToArray();

        Assert.True(invalid.Length == 0, "Generic utility files are forbidden:\n" + string.Join('\n', invalid));
    }

    private static bool IsBuildingBlock(string path) =>
        path.Replace('\\', '/').Contains("/src/BuildingBlocks/", StringComparison.OrdinalIgnoreCase);

    private static string? Context(string path)
    {
        var normalized = path.Replace('\\', '/');
        return ContextNames.FirstOrDefault(name =>
            normalized.Contains($"/src/{name}/", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ProjectLayer(string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        return name.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
    }

    [GeneratedRegex(@"\b(?:class|record|struct|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclarationRegex();
}
