namespace Aggregator.Acceptance.Contracts;

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

public sealed record AnalyticsBootstrapRequest(
    Guid PublicReadRevisionId,
    string CatalogKey,
    Guid BaseProjectionId,
    Guid PromotionOverlayId,
    Guid SafetyOverlayId,
    Guid SourcePublicationId,
    Guid ListingId,
    Guid ActorId,
    DateTimeOffset ActivatedAtUtc,
    long AccessSourceRevision);

public sealed record AnalyticsBootstrapResponse(
    Guid PublicReadRevisionId,
    Guid ListingId,
    Guid ActorId);

public sealed record AnalyticsRebuildRequest(
    DateOnly FromInclusive,
    DateOnly ToExclusive);

public sealed record AnalyticsRebuildResponse(
    DateOnly FromInclusive,
    DateOnly ToExclusive,
    int MaterializedMetricCount,
    int RemovedStaleMetricCount,
    DateTimeOffset CompletedAtUtc);
