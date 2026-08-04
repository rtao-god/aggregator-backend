namespace Aggregator.Query.Contracts;

public enum PublicListingKindContract
{
    Place = 1,
    Provider = 2,
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

public sealed record PublicListingSearchResponse(
    PublicReadMetadata Metadata,
    IReadOnlyList<PublicListingSummary> Sponsored,
    IReadOnlyList<PublicListingSummary> Organic,
    IReadOnlyList<PublicFacetValue> CategoryFacets,
    string? NextCursor);

public sealed record PublicAttributeValue(
    string AttributeKey,
    PublicFieldStateContract State,
    string? ValueKind,
    bool? BooleanValue,
    decimal? DecimalValue,
    string? TextValue,
    IReadOnlyList<string>? TextCollectionValue);

public sealed record PublicContactValue(string Kind, string Target, string? Label);

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
