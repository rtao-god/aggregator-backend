using Aggregator.Promotion.Contracts;

namespace Aggregator.Query.Application;

public sealed record PromotionOverlayInboxMessage(
    Guid EventId,
    string PayloadDigest,
    long ActivationRevision,
    DateTimeOffset ReceivedAtUtc);

public sealed record PromotionOverlayProjectionResult(
    Guid OverlayId,
    Guid SourcePublicReadRevisionId,
    long ActivationRevision,
    bool Replayed,
    bool StaleIgnored);

public interface IPromotionOverlayProjectionStore
{
    public Task<PromotionOverlayProjectionResult> ActivateAsync(
        PromotionOverlayActivated activation,
        PromotionOverlayInboxMessage inboxMessage,
        CancellationToken cancellationToken);
}

public sealed class PromotionOverlayProjectionService(
    IPromotionOverlayProjectionStore store,
    IQueryClock clock)
{
    public async Task<PromotionOverlayProjectionResult> ApplyAsync(
        PromotionOverlayActivated activation,
        string eventPayloadDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ValidateActivation(activation);
        ValidateDigest(eventPayloadDigest, "event payload");
        var receivedAtUtc = clock.GetUtcNow();
        var inbox = new PromotionOverlayInboxMessage(
            activation.EventId,
            eventPayloadDigest,
            activation.ActivationRevision,
            receivedAtUtc);
        return await store.ActivateAsync(activation, inbox, cancellationToken);
    }

    private static void ValidateActivation(PromotionOverlayActivated activation)
    {
        if (activation.EventId == Guid.Empty ||
            activation.OverlayId == Guid.Empty ||
            activation.SourcePublicReadRevisionId == Guid.Empty)
        {
            throw Failure(
                "QUERY_PROMOTION_EVENT_IDENTITY_INVALID",
                "Promotion overlay event contains an empty required identity.",
                "Correct the Promotion producer event before replaying it.");
        }

        if (string.IsNullOrWhiteSpace(activation.CatalogKey) ||
            activation.ActivationRevision <= 0)
        {
            throw Failure(
                "QUERY_PROMOTION_EVENT_CONTRACT_INVALID",
                "Promotion overlay event violates the Query projection contract.",
                "Correct the Promotion producer event before replaying it.");
        }

        ValidateDigest(activation.ContentDigest, "overlay content");
        if (activation.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_PROMOTION_EVENT_TIMESTAMP_NOT_UTC",
                "Promotion overlay event timestamp is not UTC.",
                "Correct the Promotion producer timestamp before replaying it.");
        }

        var items = activation.Items
            ?? throw Failure(
                "QUERY_PROMOTION_ITEMS_REQUIRED",
                "Promotion overlay event contains no item collection.",
                "Publish the exact bounded Promotion overlay contract.");
        if (items.Count is < 1 or > 100)
        {
            throw Failure(
                "QUERY_PROMOTION_ITEM_COUNT_INVALID",
                "Promotion overlay item count is outside the supported range.",
                "Publish between one and 100 sponsored placements.");
        }

        if (items.Any(item => item is null))
        {
            throw Failure(
                "QUERY_PROMOTION_ITEM_NULL",
                "Promotion overlay contains a null item.",
                "Republish a complete exact overlay contract.");
        }

        if (items.Select(item => item.Position).Distinct().Count() != items.Count ||
            items.Select(item => item.ListingId).Distinct().Count() != items.Count)
        {
            throw Failure(
                "QUERY_PROMOTION_ITEM_DUPLICATE",
                "Promotion overlay contains duplicate positions or listings.",
                "Correct the Promotion overlay before replaying it.");
        }

        foreach (var item in items)
        {
            if (item.ListingId == Guid.Empty ||
                item.CampaignId == Guid.Empty ||
                item.Position is < 1 or > 100 ||
                string.IsNullOrWhiteSpace(item.Locale) ||
                string.IsNullOrWhiteSpace(item.Title) ||
                string.IsNullOrWhiteSpace(item.RoutePath) ||
                !item.RoutePath.StartsWith('/') ||
                item.RoutePath.Contains("..", StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(item.DisclosureLabel))
            {
                throw Failure(
                    "QUERY_PROMOTION_ITEM_INVALID",
                    "Promotion overlay contains an invalid sponsored placement.",
                    "Correct the exact Promotion item contract before replaying it.");
            }
        }
    }

    private static void ValidateDigest(string digest, string owner)
    {
        if (string.IsNullOrWhiteSpace(digest) ||
            digest.Length != 64 ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "QUERY_PROMOTION_DIGEST_INVALID",
                $"Promotion {owner} digest is invalid.",
                "Reject the message and inspect the Promotion outbox payload.");
        }
    }

    private static QueryProjectionException Failure(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Query.PromotionProjection",
            code,
            422,
            message,
            requiredAction);
}
