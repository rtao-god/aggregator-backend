namespace Query.Infrastructure.Tests;

public sealed class PublicFacetCatalogStoreContractTests
{
    [Fact]
    public void BaseFacetCatalogReadsTheCompleteActiveProjection()
    {
        var source = Read(
            "src/Query/Query.Infrastructure/NpgsqlPublicQueryStore.Facets.cs");

        Assert.Contains("ReadFacetCatalogAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadCurrentContextAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadFacetsAsync", source, StringComparison.Ordinal);
        Assert.Contains("ReadMarketZoneFacetCountsAsync", source, StringComparison.Ordinal);
        Assert.Contains("context.Revision.BaseProjectionId", source, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_geography", source, StringComparison.Ordinal);
        Assert.Contains("GROUP BY state", source, StringComparison.Ordinal);
        Assert.DoesNotContain("afterListingId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumDocuments", source, StringComparison.Ordinal);
        Assert.DoesNotContain("@listing_ids", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LIMIT @", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SafetyFacetCatalogRecomputesEveryExposedDimension()
    {
        var facetSource = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Facets.cs");
        var pageSource = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Page.cs");

        Assert.Contains("LoadSafetyAsync", facetSource, StringComparison.Ordinal);
        Assert.Contains("ReadFacetCountsAsync", facetSource, StringComparison.Ordinal);
        Assert.Contains("ReadSafetyMarketZoneFacetCountsAsync", facetSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_geography", facetSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_category category", pageSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_geography geography", pageSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_document document", pageSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_contact contact", pageSource, StringComparison.Ordinal);
        Assert.Contains("SafetyFacetSnapshot", pageSource, StringComparison.Ordinal);
        Assert.Contains("CategoryCounts", facetSource, StringComparison.Ordinal);
        Assert.Contains("DistrictCounts", facetSource, StringComparison.Ordinal);
        Assert.Contains("ListingKindCounts", facetSource, StringComparison.Ordinal);
        Assert.Contains("ContactKindCounts", facetSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyFacetCatalogExcludesActiveListingRouteAndContactSuppressions()
    {
        var facetSource = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Facets.cs");
        var pageSource = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Page.cs");
        var combined = string.Concat(facetSource, Environment.NewLine, pageSource);

        Assert.Contains("item.target_kind = 'listing'", combined, StringComparison.Ordinal);
        Assert.Contains("item.target_kind = 'route'", combined, StringComparison.Ordinal);
        Assert.Contains("item.target_kind = 'contact'", combined, StringComparison.Ordinal);
        Assert.Contains("item.starts_at_utc <= @read_at_utc", combined, StringComparison.Ordinal);
        Assert.Contains(
            "item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc",
            combined,
            StringComparison.Ordinal);
        Assert.Contains("EnsureCatalogNotBlockedAsync", facetSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FacetCatalogReturnsTypedPersistedValuesWithoutDisplayStringReconstruction()
    {
        var baseSource = Read(
            "src/Query/Query.Infrastructure/NpgsqlPublicQueryStore.Facets.cs");
        var safetySource = Read(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Facets.cs");

        Assert.Contains("MapGeographyState", baseSource, StringComparison.Ordinal);
        Assert.Contains("QueryGeographyState", baseSource, StringComparison.Ordinal);
        Assert.Contains("NpgsqlPublicQueryStore.MapGeographyState", safetySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.Parse", baseSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.TryParse", baseSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.Parse", safetySource, StringComparison.Ordinal);
        Assert.DoesNotContain("Enum.TryParse", safetySource, StringComparison.Ordinal);
    }

    private static string Read(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(
            directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Repository file '{relativePath}' was not found.");
        return File.ReadAllText(path);
    }
}
