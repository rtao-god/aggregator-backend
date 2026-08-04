namespace Aggregator.Promotion.Contracts;

public static class PromotionOverlayContractIdentity
{
    public const string ActivationEvent = "aggregator.promotion.overlay-activated@1";

    public const string RoutingKey = "promotion.overlay.activated";
}

public sealed record PromotionOverlayItemContract(
    Guid ListingId,
    Guid CampaignId,
    int Position,
    string Locale,
    string Title,
    string RoutePath,
    string DisclosureLabel);

public sealed record PublishPromotionOverlayRequest(
    string CatalogKey,
    Guid SourcePublicReadRevisionId,
    Guid? ExpectedCurrentOverlayId,
    IReadOnlyList<PromotionOverlayItemContract> Items);

public sealed record PromotionOverlayPublicationResponse(
    Guid OverlayId,
    string CatalogKey,
    Guid SourcePublicReadRevisionId,
    long ActivationRevision,
    string ContentDigest,
    DateTimeOffset CreatedAtUtc,
    bool IsCurrent);

public sealed record PromotionOverlayActivated(
    Guid EventId,
    Guid OverlayId,
    string CatalogKey,
    Guid SourcePublicReadRevisionId,
    long ActivationRevision,
    string ContentDigest,
    IReadOnlyList<PromotionOverlayItemContract> Items,
    DateTimeOffset OccurredAtUtc);

public sealed record SponsoredListingResponse(
    Guid OverlayId,
    Guid SourcePublicReadRevisionId,
    Guid ListingId,
    Guid CampaignId,
    int Position,
    string Locale,
    string Title,
    string RoutePath,
    string DisclosureLabel);

public sealed record SponsoredListingSearchResponse(
    Guid OverlayId,
    Guid SourcePublicReadRevisionId,
    IReadOnlyList<SponsoredListingResponse> Sponsored);
