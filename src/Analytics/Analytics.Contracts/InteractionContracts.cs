namespace Aggregator.Analytics.Contracts;

public enum InteractionEventKindContract
{
    SearchResultsViewed = 1,
    ListingImpression = 2,
    ListingOpened = 3,
    WebsiteClicked = 4,
    PhoneClicked = 5,
    WhatsAppClicked = 6,
    EmailClicked = 7,
    MapClicked = 8,
    ExternalProfileClicked = 9,
    ClaimStarted = 10,
    ClaimSubmitted = 11,
}

public enum ReferrerClassContract
{
    Direct = 1,
    Internal = 2,
    Search = 3,
    Social = 4,
    Campaign = 5,
    Other = 6,
    Unknown = 7,
}

public enum ConsentModeContract
{
    EssentialOnly = 1,
    AnalyticsAllowed = 2,
}

public enum PlacementExposureKindContract
{
    Organic = 1,
    Sponsored = 2,
    NotApplicable = 3,
}

public enum TrafficQualityStateContract
{
    Accepted = 1,
    SuspectedBot = 2,
    KnownBot = 3,
    RateLimited = 4,
    Invalid = 5,
    Duplicate = 6,
}

public enum InteractionAcceptanceStateContract
{
    Accepted = 1,
    AlreadyApplied = 2,
}

public sealed record PlacementContextContract(
    PlacementExposureKindContract ExposureKind,
    Guid? PlacementId,
    string? ScopeKey);

public sealed record SubmitInteractionEventRequest(
    Guid ClientEventId,
    InteractionEventKindContract EventKind,
    string CatalogKey,
    Guid? ListingId,
    Guid PublicReadRevisionId,
    DateTimeOffset OccurredAtUtc,
    string PageContext,
    PlacementContextContract PlacementContext,
    ReferrerClassContract ReferrerClass,
    IReadOnlyDictionary<string, string> CampaignParameters,
    ConsentModeContract ConsentMode,
    string AntiAbuseToken);

public sealed record InteractionEventResponse(
    Guid EventId,
    Guid ClientEventId,
    InteractionEventKindContract EventKind,
    InteractionAcceptanceStateContract AcceptanceState,
    TrafficQualityStateContract QualityState,
    DateTimeOffset ReceivedAtUtc,
    Guid PublicReadRevisionId,
    Guid? ListingId);
