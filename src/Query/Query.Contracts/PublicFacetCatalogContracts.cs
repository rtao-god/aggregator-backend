namespace Aggregator.Query.Contracts;

/// <summary>Typed count for one public market-zone facet.</summary>
public sealed record PublicMarketZoneFacetValue(
    PublicMarketZoneContract Value,
    int Count);

/// <summary>
/// Complete safety-filtered facet catalog for one exact active public-read revision.
/// Counts are projection-wide and are not recomputed from one paged search response.
/// </summary>
public sealed record PublicFacetCatalogResponse(
    PublicReadMetadata Metadata,
    IReadOnlyList<PublicFacetValue> CategoryFacets,
    IReadOnlyList<PublicFacetValue> DistrictFacets,
    IReadOnlyList<PublicListingKindFacetValue> ListingKindFacets,
    IReadOnlyList<PublicContactKindFacetValue> ContactKindFacets,
    IReadOnlyList<PublicMarketZoneFacetValue> MarketZoneFacets);
