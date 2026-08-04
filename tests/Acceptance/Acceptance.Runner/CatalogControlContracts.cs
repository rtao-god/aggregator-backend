namespace Aggregator.Acceptance.Runner;

public sealed record CatalogSeedRequest(
    Guid SubjectId,
    Guid SubjectRevisionId,
    string Title,
    string SourceReference,
    string EvidenceDigest,
    string Website,
    decimal HourlyPrice);

public sealed record SubjectReferenceResponse(
    Guid SubjectId,
    Guid SubjectRevisionId,
    string Kind);

public sealed record CatalogSeedResponse(
    Guid ConfigurationRevisionId,
    Guid ListingId,
    Guid ListingRevisionId,
    Guid PublicationId,
    SubjectReferenceResponse Subject,
    long ExpectedListingVersionAfterPublication);

public sealed record CatalogPublishNextRequest(
    Guid ListingId,
    Guid FirstPublicationId,
    Guid SubjectId,
    Guid SubjectRevisionId,
    string Title,
    string SourceReference,
    string EvidenceDigest,
    string Website,
    decimal HourlyPrice);

public sealed record CatalogPublishNextResponse(
    Guid ListingRevisionId,
    Guid PublicationId,
    long ExpectedListingVersionAfterPublication);

public sealed record CatalogRollbackRequest(
    Guid TargetPublicationId,
    Guid ExpectedCurrentPublicationId);

public sealed record CatalogRollbackResponse(
    Guid CurrentPublicationId,
    long PublicationSequence,
    bool IsCurrent);

public sealed record QueryOrganicSnapshot(
    Guid PublicReadRevisionId,
    Guid SourcePublicationId,
    Guid ListingId,
    string Title,
    string RoutePath,
    string ResolvedLocale);

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
    long AnalyticsListingViews,
    long AnalyticsContactClicks,
    long AnalyticsLeads,
    IReadOnlyList<string> VerifiedInvariants);
