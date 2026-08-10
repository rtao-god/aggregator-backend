using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Owner snapshot for the complete active public facet catalog.</summary>
public sealed record PublicFacetCatalogSnapshot(
    PublicReadRevision Revision,
    IReadOnlyDictionary<string, int> CategoryFacetCounts,
    IReadOnlyDictionary<string, int> DistrictFacetCounts,
    IReadOnlyDictionary<QueryListingKind, int> ListingKindFacetCounts,
    IReadOnlyDictionary<QueryContactKind, int> ContactKindFacetCounts,
    IReadOnlyDictionary<QueryGeographyState, int> MarketZoneFacetCounts);

/// <summary>Reads one safety-filtered facet catalog from the exact active Query revision.</summary>
public interface IPublicFacetCatalogStore
{
    Task<PublicFacetCatalogSnapshot?> ReadAsync(
        string catalogKey,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Exposes the canonical complete facet catalog without loading or repairing listing pages.</summary>
public sealed class PublicFacetCatalogService(
    IPublicFacetCatalogStore store,
    IQueryClock clock)
{
    public async Task<PublicFacetCatalogResponse> GetAsync(
        string catalogKey,
        CancellationToken cancellationToken)
    {
        var normalizedCatalogKey = RequireKey(catalogKey, nameof(catalogKey));
        var readAtUtc = RequireUtc(clock.GetUtcNow());
        var snapshot = await store.ReadAsync(
            normalizedCatalogKey,
            readAtUtc,
            cancellationToken);
        if (snapshot is null)
        {
            throw new QueryReadException(
                "Query.PublicReadRevision",
                "QUERY_PROJECTION_UNAVAILABLE",
                503,
                $"Catalog '{normalizedCatalogKey}' has no active public read revision.",
                "Activate a valid Catalog publication and complete Query projection build.");
        }

        ValidateSnapshot(snapshot, normalizedCatalogKey);
        return new PublicFacetCatalogResponse(
            new PublicReadMetadata(
                snapshot.Revision.Id,
                snapshot.Revision.BaseProjectionId,
                snapshot.Revision.PromotionOverlayId,
                snapshot.Revision.SafetyOverlayId,
                snapshot.Revision.SourcePublicationId,
                snapshot.Revision.CreatedAtUtc),
            MapStringFacets(snapshot.CategoryFacetCounts, "category"),
            MapStringFacets(snapshot.DistrictFacetCounts, "district"),
            MapEnumFacets(
                snapshot.ListingKindFacetCounts,
                "listing-kind",
                PublicQueryContractMapper.MapListingKind,
                static (value, count) => new PublicListingKindFacetValue(value, count)),
            MapEnumFacets(
                snapshot.ContactKindFacetCounts,
                "contact-kind",
                PublicQueryContractMapper.MapContactKind,
                static (value, count) => new PublicContactKindFacetValue(value, count)),
            MapEnumFacets(
                snapshot.MarketZoneFacetCounts,
                "market-zone",
                PublicQueryContractMapper.MapMarketZone,
                static (value, count) => new PublicMarketZoneFacetValue(value, count)));
    }

    private static void ValidateSnapshot(
        PublicFacetCatalogSnapshot snapshot,
        string expectedCatalogKey)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Revision);
        ArgumentNullException.ThrowIfNull(snapshot.CategoryFacetCounts);
        ArgumentNullException.ThrowIfNull(snapshot.DistrictFacetCounts);
        ArgumentNullException.ThrowIfNull(snapshot.ListingKindFacetCounts);
        ArgumentNullException.ThrowIfNull(snapshot.ContactKindFacetCounts);
        ArgumentNullException.ThrowIfNull(snapshot.MarketZoneFacetCounts);

        if (!string.Equals(
                snapshot.Revision.CatalogKey,
                expectedCatalogKey,
                StringComparison.Ordinal))
        {
            throw StoreContractFailure(
                "Facet store returned a revision owned by another catalog.");
        }
    }

    private static IReadOnlyList<PublicFacetValue> MapStringFacets(
        IReadOnlyDictionary<string, int> facets,
        string facetKind)
    {
        if (facets.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) ||
                !string.Equals(item.Key, item.Key.Trim(), StringComparison.Ordinal) ||
                item.Value <= 0))
        {
            throw StoreContractFailure(
                $"Facet store returned an invalid {facetKind} count.");
        }

        return facets
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new PublicFacetValue(item.Key, item.Value))
            .ToArray();
    }

    private static IReadOnlyList<TContract> MapEnumFacets<TDomain, TContractValue, TContract>(
        IReadOnlyDictionary<TDomain, int> facets,
        string facetKind,
        Func<TDomain, TContractValue> mapValue,
        Func<TContractValue, int, TContract> create)
        where TDomain : struct, Enum
    {
        if (facets.Any(item => !Enum.IsDefined(item.Key) || item.Value <= 0))
        {
            throw StoreContractFailure(
                $"Facet store returned an invalid {facetKind} count.");
        }

        return facets
            .OrderBy(item => item.Key)
            .Select(item => create(mapValue(item.Key), item.Value))
            .ToArray();
    }

    private static string RequireKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_PARAMETER_REQUIRED",
                400,
                $"Parameter '{parameterName}' is required.",
                "Submit a non-empty parameter value.");
        }

        var normalized = value.Trim();
        if (normalized.Length > 200)
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_PARAMETER_TOO_LONG",
                400,
                $"Parameter '{parameterName}' exceeds 200 characters.",
                "Submit a shorter parameter value.");
        }

        return normalized;
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw StoreContractFailure(
                "Query public facet clock returned a non-UTC timestamp.");
        }

        return value;
    }

    private static QueryReadException StoreContractFailure(string message) =>
        new(
            "Query.Persistence",
            "QUERY_STORE_CONTRACT_INVALID",
            500,
            message,
            "Inspect the Query projection store and active revision before serving public traffic.");
}
