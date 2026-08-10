using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Infrastructure;

public sealed partial class SafetyAwarePublicQueryStore
{
    private static QueryListingKind MapListingKind(string value) => value switch
    {
        "place" => QueryListingKind.Place,
        "provider" => QueryListingKind.Provider,
        _ => throw StoreFailure(
            "QUERY_STORE_VALUE_UNSUPPORTED",
            $"Query persistence contains unsupported listing kind '{value}'.",
            "Restore or rebuild the Query projection using the current owner contract."),
    };

    private static QueryContactKind MapContactKind(string value) => value switch
    {
        "website" => QueryContactKind.Website,
        "email" => QueryContactKind.Email,
        "phone" => QueryContactKind.Phone,
        "whatsapp" => QueryContactKind.WhatsApp,
        "booking_reference" => QueryContactKind.BookingReference,
        "map_reference" => QueryContactKind.MapReference,
        _ => throw StoreFailure(
            "QUERY_STORE_VALUE_UNSUPPORTED",
            $"Query persistence contains unsupported contact kind '{value}'.",
            "Restore or rebuild the Query projection using the current owner contract."),
    };

    private static QueryVisibilitySuppressionTargetKind ParseTargetKind(string value)
    {
        return value switch
        {
            "listing" => QueryVisibilitySuppressionTargetKind.Listing,
            "media" => QueryVisibilitySuppressionTargetKind.Media,
            "contact" => QueryVisibilitySuppressionTargetKind.Contact,
            "route" => QueryVisibilitySuppressionTargetKind.Route,
            _ => throw StoreFailure(
                "QUERY_SAFETY_TARGET_KIND_UNSUPPORTED",
                $"Safety overlay contains unsupported target kind '{value}'.",
                "Restore or rebuild the safety overlay using the current owner contract."),
        };
    }

    private static QueryVisibilitySuppressionResponseMode ParseResponseMode(string value)
    {
        return value switch
        {
            "hide_as_not_found" => QueryVisibilitySuppressionResponseMode.HideAsNotFound,
            "gone" => QueryVisibilitySuppressionResponseMode.Gone,
            "temporarily_unavailable" => QueryVisibilitySuppressionResponseMode.TemporarilyUnavailable,
            "omit_child_element" => QueryVisibilitySuppressionResponseMode.OmitChildElement,
            _ => throw StoreFailure(
                "QUERY_SAFETY_RESPONSE_MODE_UNSUPPORTED",
                $"Safety overlay contains unsupported response mode '{value}'.",
                "Restore or rebuild the safety overlay using the current owner contract."),
        };
    }

    private sealed record SafetyFacetSnapshot(
        IReadOnlyDictionary<string, int> CategoryCounts,
        IReadOnlyDictionary<string, int> DistrictCounts,
        IReadOnlyDictionary<QueryListingKind, int> ListingKindCounts,
        IReadOnlyDictionary<QueryContactKind, int> ContactKindCounts);

    private static QueryReadException StoreFailure(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Query.Persistence",
            code,
            500,
            message,
            requiredAction);

}
