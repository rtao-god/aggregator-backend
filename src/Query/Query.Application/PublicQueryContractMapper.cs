using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Canonical mapping from Query domain identities to public wire identities.</summary>
internal static class PublicQueryContractMapper
{
    public static PublicListingKindContract MapListingKind(QueryListingKind value) => value switch
    {
        QueryListingKind.Place => PublicListingKindContract.Place,
        QueryListingKind.Provider => PublicListingKindContract.Provider,
        _ => throw StoreContractFailure($"Unsupported listing kind '{value}'."),
    };

    public static PublicContactKindContract MapContactKind(QueryContactKind value) => value switch
    {
        QueryContactKind.Website => PublicContactKindContract.Website,
        QueryContactKind.Email => PublicContactKindContract.Email,
        QueryContactKind.Phone => PublicContactKindContract.Phone,
        QueryContactKind.WhatsApp => PublicContactKindContract.WhatsApp,
        QueryContactKind.BookingReference => PublicContactKindContract.BookingReference,
        QueryContactKind.MapReference => PublicContactKindContract.MapReference,
        _ => throw StoreContractFailure($"Unsupported contact kind '{value}'."),
    };

    public static PublicMarketZoneContract MapMarketZone(QueryGeographyState value) => value switch
    {
        QueryGeographyState.PrimaryMarket => PublicMarketZoneContract.PrimaryMarket,
        QueryGeographyState.NearbyMarket => PublicMarketZoneContract.NearbyMarket,
        QueryGeographyState.RemoteOnly => PublicMarketZoneContract.RemoteOnly,
        QueryGeographyState.OutsideMarket => PublicMarketZoneContract.OutsideMarket,
        _ => throw StoreContractFailure($"Unsupported market zone '{value}'."),
    };

    private static QueryReadException StoreContractFailure(string message) =>
        new(
            "Query.Persistence",
            "QUERY_STORE_CONTRACT_INVALID",
            500,
            message,
            "Inspect the Query projection store and active revision before serving public traffic.");
}
