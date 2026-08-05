using Aggregator.Promotion.Contracts;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class PromotionOverlayProjectionServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidPlacementChangePreservesProducerIdentityAndHardExpiry()
    {
        var store = new RecordingStore();
        var service = new PromotionOverlayProjectionService(
            store,
            new FixedClock(Now));
        var change = CreateChange();

        var result = await service.ApplyAsync(
            change,
            new string('a', 64),
            CancellationToken.None);

        Assert.Equal(change.PlacementId, store.Change?.PlacementId);
        Assert.Equal(change.HardExpiryAtUtc, store.Change?.HardExpiryAtUtc);
        Assert.Equal(change.AggregateRevision, store.Change?.AggregateRevision);
        Assert.Equal(change.EventId, store.InboxMessage?.EventId);
        Assert.Equal(new string('a', 64), store.InboxMessage?.PayloadDigest);
        Assert.Equal(PromotionPlacementProjectionDisposition.Activated, result.Disposition);
    }

    [Fact]
    public async Task UnsupportedScopeFailsBeforeProjectionStore()
    {
        var store = new RecordingStore();
        var service = new PromotionOverlayProjectionService(
            store,
            new FixedClock(Now));
        var change = CreateChange() with
        {
            ScopeType = (PlacementScopeTypeContract)999,
        };

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() =>
            service.ApplyAsync(
                change,
                new string('a', 64),
                CancellationToken.None));

        Assert.Equal("QUERY_PROMOTION_SCOPE_UNSUPPORTED", exception.Code);
        Assert.Null(store.Change);
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
                CreateChange(),
                "invalid",
                CancellationToken.None));

        Assert.Equal("QUERY_PROMOTION_DIGEST_INVALID", exception.Code);
        Assert.Null(store.Change);
    }

    private static SponsoredPlacementChanged CreateChange()
    {
        var startsAtUtc = Now.AddHours(-1);
        var endsAtUtc = Now.AddDays(7);
        return new SponsoredPlacementChanged(
            Guid.Parse("0198f800-0000-7000-8000-000000000002"),
            Guid.Parse("0198f800-0000-7000-8000-000000000003"),
            Guid.Parse("0198f800-0000-7000-8000-000000000004"),
            Guid.Parse("0198f800-0000-7000-8000-000000000001"),
            "berlin-recording-services",
            "featured-listing",
            PlacementScopeTypeContract.Catalog,
            "berlin-recording-services",
            ["de-DE"],
            startsAtUtc,
            endsAtUtc,
            endsAtUtc,
            10,
            1,
            "sponsored",
            SponsoredPlacementStateContract.Active,
            7,
            Now);
    }

    private static PublicReadRevision CreateRevision(string catalogKey) =>
        PublicReadRevision.Restore(
            Guid.Parse("0198f800-0000-7000-8000-000000000010"),
            catalogKey,
            Guid.Parse("0198f800-0000-7000-8000-000000000011"),
            Guid.Parse("0198f800-0000-7000-8000-000000000012"),
            Guid.Parse("0198f800-0000-7000-8000-000000000013"),
            Guid.Parse("0198f800-0000-7000-8000-000000000014"),
            Now,
            new string('f', 64));

    private sealed class FixedClock(DateTimeOffset value) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class RecordingStore : IPromotionPlacementProjectionStore
    {
        public QueryPromotionPlacement? Change { get; private set; }

        public PromotionPlacementInboxMessage? InboxMessage { get; private set; }

        public Task<PromotionPlacementProjectionResult> ApplyAsync(
            QueryPromotionPlacement change,
            PromotionPlacementInboxMessage inboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Change = change;
            InboxMessage = inboxMessage;
            return Task.FromResult(new PromotionPlacementProjectionResult(
                CreateRevision(change.CatalogKey),
                PromotionPlacementProjectionDisposition.Activated));
        }
    }
}
