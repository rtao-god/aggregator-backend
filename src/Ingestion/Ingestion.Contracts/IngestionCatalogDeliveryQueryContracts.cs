namespace Aggregator.Ingestion.Contracts;

/// <summary>Public lifecycle of one Ingestion-owned Catalog command delivery.</summary>
public enum IngestionCatalogDeliveryStateContract
{
    Pending = 1,
    Leased = 2,
    Succeeded = 3,
    Rejected = 4,
}

/// <summary>Read-only status of one exact Catalog command delivery.</summary>
public sealed record IngestionCatalogDeliveryStatusDto(
    Guid DeliveryId,
    string ItemKey,
    string CommandType,
    string CommandDigest,
    IngestionCatalogDeliveryStateContract State,
    int AttemptCount,
    DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    Guid? CatalogListingId,
    Guid? CatalogListingRevisionId,
    string? FailureCode,
    string? FailureDetail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc);

/// <summary>Read-only delivery ledger for one exact Ingestion batch.</summary>
public sealed record IngestionCatalogDeliveriesResponse(
    Guid BatchId,
    IReadOnlyList<IngestionCatalogDeliveryStatusDto> Deliveries);
