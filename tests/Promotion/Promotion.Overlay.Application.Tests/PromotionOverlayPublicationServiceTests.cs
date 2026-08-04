using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Overlay.Application;

namespace Promotion.Overlay.Application.Tests;

public sealed class PromotionOverlayPublicationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidOverlayIsCanonicalizedAndCommittedWithExactOutboxContract()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest(
            [
                Item(position: 2, listingSuffix: 2),
                Item(position: 1, listingSuffix: 1),
            ]);

        var response = await service.PublishAsync(
            request,
            "correlation-1",
            CancellationToken.None);

        Assert.False(response.Replayed);
        Assert.NotNull(store.Publication);
        Assert.Equal([1, 2], store.Publication.Items.Select(item => item.Position));
        Assert.Equal(64, store.Publication.ContentDigest.Length);
        Assert.NotNull(store.OutboxMessage);
        Assert.Equal(PromotionOverlayContractIdentity.RoutingKey, store.OutboxMessage.RoutingKey);
        Assert.Equal(PromotionOverlayContractIdentity.ActivationEvent, store.OutboxMessage.ContractIdentity);
        Assert.Equal(64, store.OutboxMessage.PayloadDigest.Length);
    }

    [Fact]
    public async Task DuplicatePositionFailsBeforeStoreAccess()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest(
            [
                Item(position: 1, listingSuffix: 1),
                Item(position: 1, listingSuffix: 2),
            ]);

        var exception = await Assert.ThrowsAsync<PromotionOverlayException>(() =>
            service.PublishAsync(request, "correlation", CancellationToken.None));

        Assert.Equal("PROMOTION_POSITION_DUPLICATE", exception.Code);
        Assert.Equal(0, store.ActivationRevisionReadCount);
    }

    [Fact]
    public async Task TraversalRouteFailsClosed()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest(
            [Item(position: 1, listingSuffix: 1) with { RoutePath = "/de-DE/../admin" }]);

        var exception = await Assert.ThrowsAsync<PromotionOverlayException>(() =>
            service.PublishAsync(request, "correlation", CancellationToken.None));

        Assert.Equal("PROMOTION_ROUTE_INVALID", exception.Code);
        Assert.Null(store.Publication);
    }

    [Fact]
    public async Task ExactStoreReplayReturnsOriginalOverlayIdentity()
    {
        var store = new RecordingStore
        {
            Replayed = true,
        };
        var service = CreateService(store);

        var response = await service.PublishAsync(
            CreateRequest([Item(position: 1, listingSuffix: 1)]),
            "correlation",
            CancellationToken.None);

        Assert.True(response.Replayed);
        Assert.Equal(store.Publication?.OverlayId, response.OverlayId);
    }

    private static PromotionOverlayPublicationService CreateService(RecordingStore store) =>
        new(
            store,
            new QueueIdSource(
                Guid.Parse("0198f900-0000-7000-8000-000000000001"),
                Guid.Parse("0198f900-0000-7000-8000-000000000002")),
            new FixedTimeProvider(Now));

    private static PublishPromotionOverlayRequest CreateRequest(
        IReadOnlyList<PromotionOverlayItemContract> items) =>
        new(
            Guid.Parse("0198f900-0000-7000-8000-000000000010"),
            "berlin-recording-services",
            Guid.Parse("0198f900-0000-7000-8000-000000000011"),
            ExpectedCurrentOverlayId: null,
            items);

    private static PromotionOverlayItemContract Item(int position, int listingSuffix) =>
        new(
            Guid.Parse($"0198f900-0000-7000-8000-{listingSuffix:D12}"),
            Guid.Parse($"0198f900-0000-7000-9000-{listingSuffix:D12}"),
            position,
            "de-DE",
            $"Studio {listingSuffix}",
            $"/de-DE/listings/{listingSuffix}",
            "Anzeige");

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class QueueIdSource(params Guid[] values) : IPromotionOverlayIdSource
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid CreateId() => _values.Dequeue();
    }

    private sealed class RecordingStore : IPromotionOverlayStore
    {
        public bool Replayed { get; set; }

        public int ActivationRevisionReadCount { get; private set; }

        public PromotionOverlayPublication? Publication { get; private set; }

        public PromotionOverlayOutboxMessage? OutboxMessage { get; private set; }

        public Task<long> GetNextActivationRevisionAsync(
            string catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("berlin-recording-services", catalogKey);
            ActivationRevisionReadCount++;
            return Task.FromResult(7L);
        }

        public Task<PromotionOverlayCommitResult> CommitAsync(
            PromotionOverlayPublication publication,
            Guid? expectedCurrentOverlayId,
            string commandDigest,
            PromotionOverlayOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Null(expectedCurrentOverlayId);
            Assert.Equal(64, commandDigest.Length);
            Publication = publication;
            OutboxMessage = outboxMessage;
            return Task.FromResult(
                new PromotionOverlayCommitResult(publication, Replayed));
        }
    }
}
