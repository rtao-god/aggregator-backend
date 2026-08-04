using Aggregator.Promotion.Contracts;
using Aggregator.Query.Application;

namespace Query.Application.Tests;

public sealed class PromotionOverlayProjectionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidActivationPreservesExactProducerIdentity()
    {
        var store = new RecordingStore();
        var service = new PromotionOverlayProjectionService(
            store,
            new FixedClock(Now));
        var activation = CreateActivation();

        var result = await service.ApplyAsync(
            activation,
            new string('a', 64),
            CancellationToken.None);

        Assert.Equal(activation.OverlayId, result.OverlayId);
        Assert.NotNull(store.Activation);
        Assert.Equal(activation.EventId, store.InboxMessage?.EventId);
        Assert.Equal(activation.ActivationRevision, store.InboxMessage?.ActivationRevision);
        Assert.Equal(new string('a', 64), store.InboxMessage?.PayloadDigest);
    }

    [Fact]
    public async Task DuplicateListingFailsBeforeProjectionStore()
    {
        var store = new RecordingStore();
        var service = new PromotionOverlayProjectionService(
            store,
            new FixedClock(Now));
        var activation = CreateActivation();
        var duplicated = activation with
        {
            Items = [activation.Items[0], activation.Items[0] with { Position = 2 }],
        };

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() =>
            service.ApplyAsync(
                duplicated,
                new string('a', 64),
                CancellationToken.None));

        Assert.Equal("QUERY_PROMOTION_ITEM_DUPLICATE", exception.Code);
        Assert.Null(store.Activation);
    }

    [Fact]
    public async Task InvalidPayloadDigestFailsClosed()
    {
        var store = new RecordingStore();
        var service = new PromotionOverlayProjectionService(
            store,
            new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() =>
            service.ApplyAsync(
                CreateActivation(),
                "invalid",
                CancellationToken.None));

        Assert.Equal("QUERY_PROMOTION_DIGEST_INVALID", exception.Code);
        Assert.Null(store.Activation);
    }

    private static PromotionOverlayActivated CreateActivation()
    {
        var listingId = Guid.Parse("0198f800-0000-7000-8000-000000000001");
        return new PromotionOverlayActivated(
            Guid.Parse("0198f800-0000-7000-8000-000000000002"),
            Guid.Parse("0198f800-0000-7000-8000-000000000003"),
            "berlin-recording-services",
            Guid.Parse("0198f800-0000-7000-8000-000000000004"),
            7,
            new string('b', 64),
            [
                new PromotionOverlayItemContract(
                    listingId,
                    Guid.Parse("0198f800-0000-7000-8000-000000000005"),
                    1,
                    "de-DE",
                    "Gesponsertes Studio",
                    $"/de-DE/listings/{listingId:N}",
                    "Anzeige"),
            ],
            Now);
    }

    private sealed class FixedClock(DateTimeOffset value) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingStore : IPromotionOverlayProjectionStore
    {
        public PromotionOverlayActivated? Activation { get; private set; }

        public PromotionOverlayInboxMessage? InboxMessage { get; private set; }

        public Task<PromotionOverlayProjectionResult> ActivateAsync(
            PromotionOverlayActivated activation,
            PromotionOverlayInboxMessage inboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Activation = activation;
            InboxMessage = inboxMessage;
            return Task.FromResult(new PromotionOverlayProjectionResult(
                activation.OverlayId,
                activation.SourcePublicReadRevisionId,
                activation.ActivationRevision,
                Replayed: false,
                StaleIgnored: false));
        }
    }
}
