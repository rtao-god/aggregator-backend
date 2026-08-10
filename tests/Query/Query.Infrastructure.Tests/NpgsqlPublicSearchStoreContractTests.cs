namespace Query.Infrastructure.Tests;

public sealed class NpgsqlPublicSearchStoreContractTests
{
    [Fact]
    public void PublicStoreAppliesEveryTypedFilterToOrganicAndSponsoredReads()
    {
        var source = ReadRepositoryFile(
            "src/Query/Query.Infrastructure/NpgsqlPublicQueryStore.cs");

        Assert.Contains("PublicListingSearchCriteria criteria", source, StringComparison.Ordinal);
        Assert.Contains("category_filter.category_key = @category_key", source, StringComparison.Ordinal);
        Assert.Contains("district_filter.district_key = @district_key", source, StringComparison.Ordinal);
        Assert.Contains("market_zone_filter.state = @market_zone", source, StringComparison.Ordinal);
        Assert.Contains("var listingKindClause = criteria.ListingKind is null", source, StringComparison.Ordinal);
        Assert.Contains(": \"AND d.listing_kind = @listing_kind\";", source, StringComparison.Ordinal);
        Assert.Contains("contact_filter.kind = @contact_kind", source, StringComparison.Ordinal);
        Assert.Contains("@listing_kind IS NULL OR document.listing_kind = @listing_kind", source, StringComparison.Ordinal);
        Assert.Contains("item.scope_type = 'district'", source, StringComparison.Ordinal);
        Assert.Contains("item.scope_key = @district_key", source, StringComparison.Ordinal);
        Assert.Contains("if (criteria.CategoryKey is not null)", source, StringComparison.Ordinal);
        Assert.Contains("if (criteria.DistrictKey is not null)", source, StringComparison.Ordinal);
        Assert.Contains("if (criteria.ContactKind is not null)", source, StringComparison.Ordinal);
        Assert.Contains("if (criteria.MarketZone is not null)", source, StringComparison.Ordinal);
        Assert.Contains("\"market_zone\"", source, StringComparison.Ordinal);
        Assert.Contains("ToPersistedGeographyState(criteria.MarketZone.Value)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddSearchFilterParameters(command, criteria);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FacetsAreComputedFromTheCompleteActiveBaseProjection()
    {
        var source = ReadRepositoryFile(
            "src/Query/Query.Infrastructure/NpgsqlPublicQueryStore.cs");

        Assert.Contains("FROM documents.category_facet", source, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_geography", source, StringComparison.Ordinal);
        Assert.Contains("GROUP BY district_key", source, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_document", source, StringComparison.Ordinal);
        Assert.Contains("GROUP BY listing_kind", source, StringComparison.Ordinal);
        Assert.Contains("COUNT(DISTINCT listing_id)::integer", source, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_contact", source, StringComparison.Ordinal);
        Assert.Contains("new PublicFacetSnapshot(", source, StringComparison.Ordinal);

        var facetStart = source.IndexOf(
            "private static async Task<PublicFacetSnapshot> ReadFacetsAsync(",
            StringComparison.Ordinal);
        var commandHelperStart = source.IndexOf(
            "private static NpgsqlCommand CreateOwnedRowsCommand(",
            facetStart,
            StringComparison.Ordinal);
        Assert.True(facetStart >= 0 && commandHelperStart > facetStart);
        var facetOwner = source[facetStart..commandHelperStart];
        Assert.DoesNotContain("@listing_ids", facetOwner, StringComparison.Ordinal);
        Assert.DoesNotContain("@maximum_documents", facetOwner, StringComparison.Ordinal);
        Assert.DoesNotContain("after_listing_id", facetOwner, StringComparison.Ordinal);
    }

    [Fact]
    public void SafetyAwareStoreUsesTypedCriteriaAndRecomputesEveryFacetAfterSuppression()
    {
        var pageSource = ReadRepositoryFile(
            "src/Query/Query.Infrastructure/SafetyAwarePublicQueryStore.Page.cs");

        Assert.Contains("PublicListingSearchCriteria criteria", pageSource, StringComparison.Ordinal);
        Assert.Contains("InnerPageSize,\n                criteria,", pageSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_category category", pageSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_geography geography", pageSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_document document", pageSource, StringComparison.Ordinal);
        Assert.Contains("FROM documents.listing_contact contact", pageSource, StringComparison.Ordinal);
        Assert.Contains("item.target_kind = 'contact'", pageSource, StringComparison.Ordinal);
        Assert.Contains("facets.CategoryCounts", pageSource, StringComparison.Ordinal);
        Assert.Contains("facets.DistrictCounts", pageSource, StringComparison.Ordinal);
        Assert.Contains("facets.ListingKindCounts", pageSource, StringComparison.Ordinal);
        Assert.Contains("facets.ContactKindCounts", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("string? categoryKey", pageSource, StringComparison.Ordinal);
        Assert.DoesNotContain("string requestedLocale", pageSource, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedSearchMigrationOwnsCategoryDistrictKindContactAndSponsoredScopeIndexes()
    {
        var migration = ReadRepositoryFile(
            "src/Query/Query.Migrations/Migrations/V012__typed_public_search_indexes.sql");

        Assert.Contains(
            "ON documents.listing_category\n    (base_projection_id, category_key, listing_id)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ON documents.listing_geography\n    (base_projection_id, district_key, listing_id)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains("WHERE district_key IS NOT NULL", migration, StringComparison.Ordinal);
        Assert.Contains(
            "ON documents.listing_document\n    (base_projection_id, listing_kind, listing_id)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ON documents.listing_contact\n    (base_projection_id, kind, listing_id)",
            migration,
            StringComparison.Ordinal);
        Assert.Contains(
            "ON projection.promotion_overlay_item\n    (overlay_id, scope_type, scope_key, placement_id)",
            migration,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarketZoneSearchIndexIsOwnedByQueryMigration()
    {
        var migration = ReadRepositoryFile(
            "src/Query/Query.Migrations/Migrations/V016__market_zone_search_index.sql");

        Assert.Contains(
            "ON documents.listing_geography\n    (base_projection_id, state, listing_id)",
            migration,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(
            directory!.FullName,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
