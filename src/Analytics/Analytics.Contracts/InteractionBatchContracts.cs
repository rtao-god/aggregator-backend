namespace Aggregator.Analytics.Contracts;

/// <summary>Declares the explicit result state for one item in a bounded interaction batch.</summary>
public enum InteractionEventBatchItemStateContract
{
    Accepted = 1,
    AlreadyApplied = 2,
    Rejected = 3,
}

/// <summary>Submits independent interaction events under one bounded transport request.</summary>
public sealed record SubmitInteractionEventBatchRequest(
    IReadOnlyList<SubmitInteractionEventRequest> Events);

/// <summary>Preserves the owner failure for one rejected batch item.</summary>
public sealed record InteractionEventBatchItemFailureContract(
    string Owner,
    string Code,
    int StatusCode,
    string Detail,
    string RequiredAction);

/// <summary>Returns the exact outcome of one indexed batch item without hiding partial acceptance.</summary>
public sealed record InteractionEventBatchItemResponse(
    int Index,
    Guid ClientEventId,
    InteractionEventKindContract EventKind,
    InteractionEventBatchItemStateContract State,
    InteractionEventResponse? Event,
    InteractionEventBatchItemFailureContract? Failure);

/// <summary>Returns all independent item outcomes for one bounded interaction batch.</summary>
public sealed record InteractionEventBatchResponse(
    int AcceptedCount,
    int AlreadyAppliedCount,
    int RejectedCount,
    IReadOnlyList<InteractionEventBatchItemResponse> Items);
