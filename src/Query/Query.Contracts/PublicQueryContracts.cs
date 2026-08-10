namespace Aggregator.Query.Contracts;

public enum PublicListingKindContract
{
    Place = 1,
    Provider = 2,
}

public enum PublicContactKindContract
{
    Website = 1,
    Email = 2,
    Phone = 3,
    WhatsApp = 4,
    BookingReference = 5,
    MapReference = 6,
}

public enum PublicFieldStateContract
{
    Observed = 1,
    Missing = 2,
    NotApplicable = 3,
    Withheld = 4,
}

public sealed record PublicReadMetadata(
    Guid PublicReadRevisionId,
    Guid BaseProjectionId,
    Guid PromotionOverlayId,
    Guid SafetyOverlayId,
    Guid SourcePublicationId,
    DateTimeOffset GeneratedAtUtc);

public sealed record PublicListingSearchRequest(
    string Locale,
    string? CategoryKey,
    string? DistrictKey,
    PublicListingKindContract? ListingKind,
    PublicContactKindContract? ContactKind,
    int PageSize,
    string? Cursor);

public sealed record PublicListingSearchQuerySummary(
    string RequestedLocale,
    string? CategoryKey,
    string? DistrictKey,
    PublicListingKindContract? ListingKind,
    PublicContactKindContract? ContactKind);

public sealed record PublicListingSummary(
    Guid ListingId,
    Guid ListingRevisionId,
    PublicListingKindContract ListingKind,
    string RequestedLocale,
    string ResolvedLocale,
    string TranslationState,
    string RoutePath,
    string Title,
    PublicFieldStateContract DescriptionState,
    string? Description,
    IReadOnlyList<string> CategoryKeys,
    string? DistrictKey);

public sealed record PublicFacetValue(string Key, int Count);

public sealed record PublicListingKindFacetValue(
    PublicListingKindContract Value,
    int Count);

public sealed record PublicContactKindFacetValue(
    PublicContactKindContract Value,
    int Count);

public sealed record PublicSponsoredListingSummary(
    Guid PlacementId,
    Guid EntitlementId,
    string ProductKey,
    string ScopeType,
    string ScopeKey,
    int PriorityBand,
    int CapacitySlot,
    string DisclosureLabelKey,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset HardExpiryAtUtc,
    PublicListingSummary Listing);

public sealed record PublicListingSearchResponse(
    PublicReadMetadata Metadata,
    PublicListingSearchQuerySummary Query,
    IReadOnlyList<PublicSponsoredListingSummary> Sponsored,
    IReadOnlyList<PublicListingSummary> Organic,
    IReadOnlyList<PublicFacetValue> CategoryFacets,
    IReadOnlyList<PublicFacetValue> DistrictFacets,
    IReadOnlyList<PublicListingKindFacetValue> ListingKindFacets,
    IReadOnlyList<PublicContactKindFacetValue> ContactKindFacets,
    string? NextCursor);

public sealed record PublicAttributeValue(
    string AttributeKey,
    PublicFieldStateContract State,
    string? ValueKind,
    bool? BooleanValue,
    decimal? DecimalValue,
    string? TextValue,
    IReadOnlyList<string>? TextCollectionValue);

public sealed record PublicContactValue(Guid ContactId, string Kind, string Target, string? Label);

public sealed record PublicMediaValue(
    Guid MediaId,
    string ObjectUri,
    string ContentType,
    string ContentDigest,
    string RightsBasis);

public sealed record PublicGeographyValue(
    string State,
    decimal? Latitude,
    decimal? Longitude,
    string? DistrictKey);

public sealed record PublicListingCardResponse(
    PublicReadMetadata Metadata,
    PublicListingSummary Listing,
    IReadOnlyList<PublicAttributeValue> Attributes,
    PublicGeographyValue Geography,
    IReadOnlyList<PublicContactValue> Contacts,
    IReadOnlyList<PublicMediaValue> Media);
