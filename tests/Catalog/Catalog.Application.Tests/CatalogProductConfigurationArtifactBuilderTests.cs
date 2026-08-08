using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;

namespace Catalog.Application.Tests;

public sealed class CatalogProductConfigurationArtifactBuilderTests
{
    private static readonly Guid RevisionId =
        Guid.Parse("0192f5f0-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const string ExpectedDigest =
        "ac8b665d754ac21f0ec66fab729e1a669de53effaed4120a6e004dc9f7785f31";

    [Fact]
    public void CompleteAuthoredConfigurationProducesExactImportArtifact()
    {
        var artifact = CatalogProductConfigurationArtifactBuilder.BuildImportRequest(
            CreateConfiguration(["recording-studio"]));

        Assert.Equal(CatalogContractIdentity.ProductConfiguration, artifact.ContractIdentity);
        Assert.Equal(CatalogContractIdentity.ProductConfigurationRevision, artifact.ContractRevision);
        Assert.Equal(ExpectedDigest, artifact.ExpectedContentDigest);
        Assert.Equal(RevisionId, artifact.Configuration.RevisionId);
    }

    [Fact]
    public void AttributeReferencingUnknownCategoryFailsAtCatalogOwnerBoundary()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CatalogProductConfigurationArtifactBuilder.BuildImportRequest(
                CreateConfiguration(["unknown-category"])));

        Assert.Equal("attributes", exception.ParamName);
        Assert.Contains("unknown-category", exception.Message, StringComparison.Ordinal);
        Assert.Contains("hourly-price", exception.Message, StringComparison.Ordinal);
    }

    private static ProductConfigurationContract CreateConfiguration(
        IReadOnlyList<string> attributeCategories) =>
        new(
            RevisionId,
            CreatedAtUtc,
            new SiteDefinitionContract(
                "berlin-recording",
                "de-DE",
                ["en-GB", "de-DE"],
                "EUR",
                "Europe/Berlin"),
            new CatalogDefinitionContract(
                "berlin-recording-services",
                "berlin-recording",
                "berlin-core-and-nearby",
                "EUR",
                "Europe/Berlin",
                [SubjectKindContract.Provider, SubjectKindContract.Place]),
            [
                Category(
                    "recording-studio",
                    SubjectKindContract.Place,
                    "Tonstudio",
                    "Recording studio"),
                Category(
                    "music-producer",
                    SubjectKindContract.Provider,
                    "Musikproduzent",
                    "Music producer"),
            ],
            [
                new AttributeDefinitionContract(
                    "hourly-price",
                    AttributeValueKindContract.Decimal,
                    AttributeCardinalityContract.Single,
                    PublicFieldRequirementContract.Optional,
                    attributeCategories,
                    Localized("Stundenpreis", "Hourly price"),
                    Minimum: null,
                    Maximum: null,
                    AllowedValues: [],
                    IsFilterable: true,
                    IsSortable: true),
            ]);

    private static CategoryDefinitionContract Category(
        string key,
        SubjectKindContract subjectKind,
        string german,
        string english) =>
        new(key, [subjectKind], Localized(german, english), IsActive: true);

    private static Dictionary<string, string> Localized(
        string german,
        string english) =>
        new(StringComparer.Ordinal)
        {
            ["de-DE"] = german,
            ["en-GB"] = english,
        };
}
