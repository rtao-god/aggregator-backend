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
        Assert.Contains("@listing_kind IS NULL OR d.listing_kind = @listing_kind", source, StringComparison.Ordinal);
        Assert.Contains("contact_filter.kind = @contact_kind", source, StringComparison.Ordinal);
        Assert.Contains("@listing_kind IS NULL OR document.listing_kind = @listing_kind", source, StringComparison.Ordinal);
        Assert.Contains("item.scope_type = 'district'", source, StringComparison.Ordinal);
        Assert.Contains("item.scope_key = @district_key", source, StringComparison.Ordinal);
        Assert.Contains("AddSearchFilterParameters(command, criteria);", source, StringComparison.Ordinal);
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
