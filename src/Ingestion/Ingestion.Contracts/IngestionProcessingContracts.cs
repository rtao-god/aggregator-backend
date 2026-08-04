using System.ComponentModel.DataAnnotations;

namespace Aggregator.Ingestion.Contracts;

public enum IngestionProcessingDecisionContract
{
    Accepted = 1,
    NeedsReview = 2,
    Rejected = 3,
}

public sealed record IngestionProcessingItemDecisionDto(
    Guid DecisionId,
    string ItemKey,
    string ItemDigest,
    IngestionProcessingDecisionContract Decision,
    IReadOnlyList<string> ReasonCodes,
    Guid? SupersedesDecisionId,
    DateTimeOffset DecidedAtUtc,
    string DecidedBy);

public sealed record IngestionBatchProcessingResponse(
    Guid BatchId,
    string State,
    long AggregateRevision,
    int ExpectedItemCount,
    int AcceptedItemCount,
    int ReviewRequiredItemCount,
    int RejectedItemCount,
    IReadOnlyList<IngestionProcessingItemDecisionDto> Decisions);

public sealed record ReviewIngestionItemRequest(
    [property: Required, MaxLength(200)]
    string ItemKey,
    Guid ExpectedDecisionId,
    IngestionProcessingDecisionContract Decision,
    [property: Required, MaxLength(200)]
    string ReasonCode);

public sealed record CompleteIngestionReviewRequest(
    long ExpectedAggregateRevision,
    [property: MinLength(1)]
    IReadOnlyList<ReviewIngestionItemRequest> Decisions);

public sealed record CommitIngestionBatchRequest(long ExpectedAggregateRevision);

public sealed record IngestionCatalogDeliveryDto(
    Guid DeliveryId,
    string ItemKey,
    string CommandType,
    string CommandDigest,
    string State,
    int AttemptCount,
    Guid? CatalogListingId,
    Guid? CatalogListingRevisionId,
    string? FailureCode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc);

public sealed record IngestionCommitResponse(
    Guid BatchId,
    string State,
    long AggregateRevision,
    IReadOnlyList<IngestionCatalogDeliveryDto> Deliveries,
    bool Replayed);
