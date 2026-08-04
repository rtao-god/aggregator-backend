namespace Aggregator.Analytics.Contracts;

public enum AnalyticsInteractionKindContract
{
    PageView = 1,
    ListingView = 2,
    ContactClick = 3,
    Lead = 4,
}

public sealed record RecordAnalyticsInteractionRequest(
    Guid EventId,
    string CatalogKey,
    Guid PublicReadRevisionId,
    Guid? ListingId,
    string SessionKey,
    AnalyticsInteractionKindContract Kind,
    DateTimeOffset OccurredAtUtc);

public sealed record AnalyticsInteractionReceipt(
    Guid EventId,
    DateTimeOffset RecordedAtUtc,
    bool Replayed);

public sealed record AnalyticsListingMetricsResponse(
    string CatalogKey,
    Guid ListingId,
    long ListingViews,
    long ContactClicks,
    long Leads,
    DateTimeOffset UpdatedAtUtc);
