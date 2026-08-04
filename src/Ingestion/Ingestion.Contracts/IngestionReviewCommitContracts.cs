using System.ComponentModel.DataAnnotations;

namespace Aggregator.Ingestion.Contracts;

public enum IngestionItemDecisionContract
{
    Accepted = 1,
    NeedsReview = 2,
    Rejected = 3,
}

public enum IngestionCatalogDeliveryOutcomeContract
{
    Delivered = 1,
    Rejected = 2,
}

public sealed record ResolveIngestionReviewItemRequest(
    [property: Required, StringLength(300, MinimumLength = 1)] string ItemKey,
    IngestionItemDecisionContract Decision,
    [property: Required, MinLength(1)] IReadOnlyList<string> ReasonCodes);

public sealed record CompleteIngestionReviewRequest(
    [property: Range(1, long.MaxValue)] long ExpectedAggregateRevision,
    [property: Required, MinLength(1)] IReadOnlyList<ResolveIngestionReviewItemRequest> Items);

public sealed record BeginIngestionCommitRequest(
    [property: Range(1, long.MaxValue)] long ExpectedAggregateRevision,
    [property: Required, MinLength(1)] IReadOnlyList<string> SelectedItemKeys);

public sealed record RecordIngestionCatalogOutcomeRequest(
    [property: Required, StringLength(300, MinimumLength = 1)] string ItemKey,
    [property: Required] Guid CommandId,
    IngestionCatalogDeliveryOutcomeContract Outcome,
    Guid? CatalogSubjectId,
    Guid? CatalogListingId,
    Guid? CatalogListingRevisionId,
    [property: StringLength(200)] string? FailureCode);

public sealed record CompleteIngestionCommitRequest(
    [property: Range(1, long.MaxValue)] long ExpectedAggregateRevision,
    [property: Required, MinLength(1)] IReadOnlyList<RecordIngestionCatalogOutcomeRequest> Outcomes);

public sealed record IngestionWorkflowCommandResponse(
    IngestionBatchDto Batch,
    bool Replayed);
