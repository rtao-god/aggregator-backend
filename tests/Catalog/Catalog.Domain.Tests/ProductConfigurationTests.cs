using System.Collections.Immutable;
using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class ProductConfigurationTests
{
    [Fact]
    public void DuplicateLocalizedSlugIsRejected()
    {
        var site = CreateSite();
        var catalog = CreateCatalog();
        var categories = new[]
        {
            CreateCategory("recording-studio", "studio"),
            CreateCategory("podcast-studio", "studio"),
        };

        var exception = Assert.Throws<CatalogDomainException>(() =>
            ProductConfigurationDefinition.Create(
                site,
                catalog,
                categories,
                Array.Empty<AttributeDefinition>(),
                Array.Empty<CategoryAttributeDefinition>()));

        Assert.Equal("CATEGORY_SLUG_DUPLICATE", exception.Code);
    }

    [Fact]
    public void UnknownCategoryAttributeReferenceIsRejected()
    {
        var relation = new CategoryAttributeDefinition(
            "missing-category",
            "parking",
            RequiredForDraft: false,
            RequiredForPublication: true,
            FilterableInCategory: true,
            Comparable: false,
            VisibleInCard: true,
            ImmutableHashSet.Create(ConfiguredListingKind.Place),
            "access",
            1);

        var exception = Assert.Throws<CatalogDomainException>(() =>
            ProductConfigurationDefinition.Create(
                CreateSite(),
                CreateCatalog(),
                [CreateCategory("recording-studio", "recording-studio")],
                [CreateAttribute()],
                [relation]));

        Assert.Equal("CATEGORY_ATTRIBUTE_CATEGORY_UNKNOWN", exception.Code);
    }

    private static SiteConfigurationDefinition CreateSite() =>
        new(
            "test-site",
            "en-GB",
            ["en-GB"],
            "EUR",
            "Europe/Berlin",
            "test-brand",
            ["catalog.example.test"],
            ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["privacy"] = "/privacy",
            }));

    private static CatalogConfigurationDefinition CreateCatalog() =>
        new(
            "test-catalog",
            "test-site",
            ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["en-GB"] = "Test catalog",
            }),
            "test-market",
            "EUR",
            "Europe/Berlin",
            ImmutableHashSet.Create(ConfiguredListingKind.Place),
            "default-seo",
            "default-publication",
            "default-contact",
            "default-claim",
            "default-promotion");

    private static CategoryDefinition CreateCategory(string key, string slug) =>
        new(
            key,
            null,
            ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["en-GB"] = key,
            }),
            ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["en-GB"] = slug,
            }),
            ImmutableHashSet.Create(ConfiguredListingKind.Place),
            PrimaryAllowed: true,
            SeoIndexable: true,
            SortOrder: 1);

    private static AttributeDefinition CreateAttribute() =>
        new(
            "parking",
            ConfiguredAttributeDataType.Boolean,
            Multiple: false,
            Filterable: true,
            Comparable: false,
            Sortable: false,
            Public: true,
            ImmutableArray<string>.Empty,
            ImmutableDictionary.CreateRange(new Dictionary<string, string>
            {
                ["en-GB"] = "Parking",
            }));
}
