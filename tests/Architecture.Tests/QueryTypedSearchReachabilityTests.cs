using Xunit;

namespace Architecture.Tests;

public sealed class QueryTypedSearchReachabilityTests
{
    [Fact]
    public void TypedFiltersReachFromPublicContractToPostgreSqlOwner()
    {
        var repository = RepositoryModel.Load();
        var contracts = Read(repository, "src/Query/Query.Contracts/PublicQueryContracts.cs");
        var controller = Read(repository, "src/Query/Query.Api/CatalogQueryController.cs");
        var ports = Read(repository, "src/Query/Query.Application/PublicQueryPorts.cs");
        var service = Read(repository, "src/Query/Query.Application/PublicQueryService.cs");
        var store = Read(repository, "src/Query/Query.Infrastructure/NpgsqlPublicQueryStore.cs");

        Assert.Contains("public sealed record PublicListingSearchRequest(", contracts, StringComparison.Ordinal);
        Assert.Contains("string? DistrictKey,", contracts, StringComparison.Ordinal);
        Assert.Contains("PublicListingKindContract? ListingKind,", contracts, StringComparison.Ordinal);
        Assert.Contains("PublicContactKindContract? ContactKind,", contracts, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<PublicFacetValue> DistrictFacets,", contracts, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<PublicListingKindFacetValue> ListingKindFacets,", contracts, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<PublicContactKindFacetValue> ContactKindFacets,", contracts, StringComparison.Ordinal);

        Assert.Contains("SupportedSearchParameters", controller, StringComparison.Ordinal);
        Assert.Contains("\"district\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"listingKind\"", controller, StringComparison.Ordinal);
        Assert.Contains("\"contactKind\"", controller, StringComparison.Ordinal);
        Assert.Contains("QUERY_FILTER_UNKNOWN", controller, StringComparison.Ordinal);
        Assert.Contains("ParseListingKind(listingKind)", controller, StringComparison.Ordinal);
        Assert.Contains("ParseContactKind(contactKind)", controller, StringComparison.Ordinal);

        Assert.Contains("public sealed record PublicListingSearchCriteria(", ports, StringComparison.Ordinal);
        Assert.Contains("PublicListingSearchCriteria criteria,", ports, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyDictionary<string, int> DistrictFacetCounts,", ports, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyDictionary<QueryListingKind, int> ListingKindFacetCounts,", ports, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyDictionary<QueryContactKind, int> ContactKindFacetCounts);", ports, StringComparison.Ordinal);

        Assert.Contains("CreateSearchCriteria(request)", service, StringComparison.Ordinal);
        Assert.Contains("EnsureDocumentMatchesCriteria(document, criteria)", service, StringComparison.Ordinal);
        Assert.Contains("QueryPromotionPlacementScope.District", service, StringComparison.Ordinal);
        Assert.Contains("snapshot.DistrictFacetCounts", service, StringComparison.Ordinal);
        Assert.Contains("snapshot.ListingKindFacetCounts", service, StringComparison.Ordinal);
        Assert.Contains("snapshot.ContactKindFacetCounts", service, StringComparison.Ordinal);

        Assert.Contains("AddSearchFilterParameters(command, criteria);", store, StringComparison.Ordinal);
        Assert.Contains("category_filter.category_key = @category_key", store, StringComparison.Ordinal);
        Assert.Contains("district_filter.district_key = @district_key", store, StringComparison.Ordinal);
        Assert.Contains("contact_filter.kind = @contact_kind", store, StringComparison.Ordinal);
        Assert.Contains("ReadFacetsAsync(", store, StringComparison.Ordinal);
    }

    [Fact]
    public void CursorDigestOwnsEveryNormalizedFilter()
    {
        var repository = RepositoryModel.Load();
        var cursor = Read(repository, "src/Query/Query.Application/QueryCursorCodec.cs");

        Assert.Contains("PublicListingSearchCriteria criteria", cursor, StringComparison.Ordinal);
        Assert.Contains("criteria.RequestedLocale", cursor, StringComparison.Ordinal);
        Assert.Contains("criteria.CategoryKey", cursor, StringComparison.Ordinal);
        Assert.Contains("criteria.DistrictKey", cursor, StringComparison.Ordinal);
        Assert.Contains("criteria.ListingKind", cursor, StringComparison.Ordinal);
        Assert.Contains("criteria.ContactKind", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("string? categoryKey,", cursor, StringComparison.Ordinal);
    }

    [Fact]
    public void TypedFilterIndexesRemainOwnedByQueryMigrations()
    {
        var repository = RepositoryModel.Load();
        var migration = Read(
            repository,
            "src/Query/Query.Migrations/Migrations/V012__typed_public_search_indexes.sql");

        Assert.Contains("ix_query_listing_category_search", migration, StringComparison.Ordinal);
        Assert.Contains("ix_query_listing_district_search", migration, StringComparison.Ordinal);
        Assert.Contains("ix_query_listing_kind_search", migration, StringComparison.Ordinal);
        Assert.Contains("ix_query_listing_contact_kind_search", migration, StringComparison.Ordinal);
        Assert.Contains("ix_query_promotion_overlay_scope_search", migration, StringComparison.Ordinal);
    }

    private static string Read(RepositoryModel repository, string relativePath) =>
        File.ReadAllText(Path.Combine(
            repository.Root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
}
