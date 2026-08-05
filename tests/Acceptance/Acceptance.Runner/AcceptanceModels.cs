namespace Aggregator.Acceptance.Runner;

public sealed record QueryOrganicSnapshot(
    Guid PublicReadRevisionId,
    Guid BaseProjectionId,
    Guid PromotionOverlayId,
    Guid SafetyOverlayId,
    Guid SourcePublicationId,
    Guid ListingId,
    string Title,
    string RoutePath,
    string ResolvedLocale,
    DateTimeOffset GeneratedAtUtc);

public sealed record AcceptanceReport(
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    Guid CollectorCandidateId,
    Guid ListingId,
    Guid FirstPublicationId,
    Guid FirstPublicReadRevisionId,
    Guid FirstPromotionOverlayId,
    Guid SecondPublicationId,
    Guid SecondPublicReadRevisionId,
    Guid RollbackPublicReadRevisionId,
    Guid RollbackPromotionOverlayId,
    long AnalyticsOrganicImpressions,
    long AnalyticsListingOpens,
    long AnalyticsWebsiteClicks,
    int AnalyticsMaterializedMetricCount,
    IReadOnlyList<string> VerifiedInvariants);
