namespace Aggregator.Ingestion.Contracts;

public enum ImportBatchStateContract
{
    Registered = 1,
    Uploading = 2,
    Uploaded = 3,
    IntegrityChecking = 4,
    IntegrityValid = 5,
    ItemValidation = 6,
    ReviewRequired = 7,
    ReadyToCommit = 8,
    Committing = 9,
    Committed = 10,
    PartiallyRejected = 11,
    Superseded = 12,
    IntegrityFailed = 13,
    ContractRejected = 14,
    BlockedByPolicy = 15,
    CommitFailed = 16,
    Expired = 17,
    Cancelled = 18,
}

/// <summary>Registers one exact backend-owned candidate-ingestion manifest.</summary>
public sealed record RegisterIngestionBatchRequest(
    AggregatorCandidateIngestionManifest Manifest,
    string ManifestDigest);

/// <summary>Read-only transport projection of one Ingestion-owned import batch.</summary>
public sealed record IngestionBatchDto(
    Guid Id,
    string ProducerIdentity,
    string ProducerBuild,
    Guid CollectorExportId,
    string CollectorExportDigest,
    string TargetSiteKey,
    string TargetCatalogKey,
    Guid TargetCatalogConfigurationRevisionId,
    int ExpectedItemCount,
    string ManifestDigest,
    string ItemIndexDigest,
    string PayloadDigest,
    string PayloadObjectKey,
    string PayloadObjectDigest,
    long PayloadObjectSize,
    string PayloadContentType,
    DateTimeOffset RegisteredAtUtc,
    DateTimeOffset LastChangedAtUtc,
    ImportBatchStateContract State,
    long AggregateRevision,
    int AcceptedItemCount,
    int ReviewRequiredItemCount,
    int RejectedItemCount,
    string? FailureCode);

public sealed record IngestionBatchRegistrationResponse(
    IngestionBatchDto Batch,
    bool Replayed);
