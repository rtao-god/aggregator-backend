using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>Processes a bounded set of independent interaction events and exposes every item outcome.</summary>
public sealed class SubmitInteractionEventBatchService(
    SubmitInteractionEventService interactionService)
{
    public const int MaximumEventCount = 50;

    public async Task<InteractionEventBatchResponse> SubmitAsync(
        SubmitInteractionEventBatchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Events is null)
        {
            throw InvalidBatch(
                "ANALYTICS_INTERACTION_BATCH_EVENTS_REQUIRED",
                "Interaction batch events are required.");
        }

        if (request.Events.Count is < 1 or > MaximumEventCount)
        {
            throw InvalidBatch(
                "ANALYTICS_INTERACTION_BATCH_COUNT_INVALID",
                $"Interaction batch count must be between 1 and {MaximumEventCount}.");
        }

        ValidateUniqueSemanticIdentities(request.Events);

        var items = new List<InteractionEventBatchItemResponse>(request.Events.Count);
        var acceptedCount = 0;
        var alreadyAppliedCount = 0;
        var rejectedCount = 0;
        for (var index = 0; index < request.Events.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = request.Events[index]
                ?? throw InvalidBatch(
                    "ANALYTICS_INTERACTION_BATCH_ITEM_REQUIRED",
                    $"Interaction batch item at index '{index}' is required.");
            try
            {
                var result = await interactionService.SubmitAsync(item, cancellationToken);
                var state = result.AcceptanceState switch
                {
                    InteractionAcceptanceStateContract.Accepted =>
                        InteractionEventBatchItemStateContract.Accepted,
                    InteractionAcceptanceStateContract.AlreadyApplied =>
                        InteractionEventBatchItemStateContract.AlreadyApplied,
                    _ => throw InvalidAcceptanceState(result.AcceptanceState),
                };
                if (state == InteractionEventBatchItemStateContract.Accepted)
                {
                    acceptedCount++;
                }
                else
                {
                    alreadyAppliedCount++;
                }

                items.Add(new InteractionEventBatchItemResponse(
                    index,
                    item.ClientEventId,
                    item.EventKind,
                    state,
                    result,
                    Failure: null));
            }
            catch (AnalyticsCommandException exception) when (
                exception.StatusCode is >= 400 and < 500)
            {
                rejectedCount++;
                items.Add(new InteractionEventBatchItemResponse(
                    index,
                    item.ClientEventId,
                    item.EventKind,
                    InteractionEventBatchItemStateContract.Rejected,
                    Event: null,
                    new InteractionEventBatchItemFailureContract(
                        exception.Owner,
                        exception.Code,
                        exception.StatusCode,
                        exception.Message,
                        exception.RequiredAction)));
            }
        }

        return new InteractionEventBatchResponse(
            acceptedCount,
            alreadyAppliedCount,
            rejectedCount,
            items);
    }

    private static void ValidateUniqueSemanticIdentities(
        IReadOnlyList<SubmitInteractionEventRequest> events)
    {
        var identities = new HashSet<InteractionEventSemanticKey>();
        for (var index = 0; index < events.Count; index++)
        {
            var item = events[index]
                ?? throw InvalidBatch(
                    "ANALYTICS_INTERACTION_BATCH_ITEM_REQUIRED",
                    $"Interaction batch item at index '{index}' is required.");
            InteractionEventSemanticKey identity;
            try
            {
                identity = InteractionEventSemanticKey.Create(
                    item.ClientEventId,
                    AnalyticsContractMapper.ToDomain(item.EventKind));
            }
            catch (AnalyticsDomainException exception)
            {
                throw InvalidBatch(exception.Code, exception.Message);
            }

            if (!identities.Add(identity))
            {
                throw new AnalyticsCommandException(
                    "Analytics.Events",
                    "ANALYTICS_INTERACTION_BATCH_SEMANTIC_IDENTITY_DUPLICATE",
                    409,
                    $"Interaction batch contains duplicate semantic identity at index '{index}'.",
                    "Send each client event ID and event kind at most once per batch.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["index"] = index,
                        ["clientEventId"] = item.ClientEventId,
                        ["eventKind"] = item.EventKind.ToString(),
                    });
            }
        }
    }

    private static AnalyticsCommandException InvalidBatch(string code, string message) =>
        new(
            "Analytics.Events",
            code,
            400,
            message,
            "Correct the bounded interaction batch and resubmit it with the same item identities.");

    private static AnalyticsCommandException InvalidAcceptanceState(
        InteractionAcceptanceStateContract state) =>
        new(
            "Analytics.Events",
            "ANALYTICS_INTERACTION_BATCH_ITEM_STATE_INVALID",
            500,
            $"Interaction service returned unsupported acceptance state '{state}'.",
            "Stop batch intake and repair the Analytics interaction service contract.");
}
