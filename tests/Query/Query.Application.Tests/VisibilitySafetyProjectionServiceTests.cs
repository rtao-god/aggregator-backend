using Aggregator.Catalog.Contracts;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class VisibilitySafetyProjectionServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ActiveCatalogEventMapsToExactQuerySuppression()
    {
        var expectedRevision = CurrentRevision();
        var store = new RecordingStore(expectedRevision);
        var service = new VisibilitySafetyProjectionService(
            store,
            new FixedClock(Timestamp.AddMinutes(1)));
        var listingId = Guid.Parse("0198fc00-0000-7000-8000-000000000001");
        var change = new CatalogPublicVisibilitySuppressionChanged(
            Guid.Parse("0198fc00-0000-7000-8000-000000000002"),
            Guid.Parse("0198fc00-0000-7000-8000-000000000003"),
            "berlin-recording-services",
            new PublicVisibilitySuppressionTargetContract(
                PublicVisibilitySuppressionTargetKindContract.Listing,
                listingId,
                listingId.ToString("D")),
            "legal-removal",
            PublicVisibilitySuppressionResponseModeContract.Gone,
            PublicVisibilitySuppressionStateContract.Active,
            Timestamp,
            Timestamp.AddDays(1),
            2,
            Timestamp);

        var result = await service.ApplyAsync(
            change,
            new string('a', 64),
            CancellationToken.None);

        Assert.Equal(expectedRevision, result.PublicReadRevision);
        var suppression = Assert.IsType<QueryVisibilitySuppression>(store.Suppression);
        Assert.Equal(change.SuppressionId, suppression.SuppressionId);
        Assert.Equal(QueryVisibilitySuppressionTargetKind.Listing, suppression.TargetKind);
        Assert.Equal(listingId, suppression.ListingId);
        Assert.Equal(QueryVisibilitySuppressionResponseMode.Gone, suppression.ResponseMode);
        Assert.Equal(QueryVisibilitySuppressionState.Active, suppression.State);
        Assert.Equal(2, suppression.AggregateRevision);
        Assert.Equal(change.EventId, store.InboxMessage?.EventId);
        Assert.Equal(Timestamp.AddMinutes(1), store.InboxMessage?.ReceivedAtUtc);
    }

    [Fact]
    public async Task RequestedCatalogStateIsRejectedBeforePersistence()
    {
        var store = new RecordingStore(CurrentRevision());
        var service = new VisibilitySafetyProjectionService(store, new FixedClock(Timestamp));
        var listingId = Guid.Parse("0198fc00-0000-7000-8000-000000000010");
        var change = new CatalogPublicVisibilitySuppressionChanged(
            Guid.Parse("0198fc00-0000-7000-8000-000000000011"),
            Guid.Parse("0198fc00-0000-7000-8000-000000000012"),
            "berlin-recording-services",
            new PublicVisibilitySuppressionTargetContract(
                PublicVisibilitySuppressionTargetKindContract.Listing,
                listingId,
                listingId.ToString("D")),
            "privacy-request",
            PublicVisibilitySuppressionResponseModeContract.HideAsNotFound,
            PublicVisibilitySuppressionStateContract.Requested,
            Timestamp,
            null,
            1,
            Timestamp);

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() =>
            service.ApplyAsync(change, new string('b', 64), CancellationToken.None));

        Assert.Equal("QUERY_VISIBILITY_REQUESTED_EVENT_FORBIDDEN", exception.Code);
        Assert.Null(store.Suppression);
        Assert.Null(store.InboxMessage);
    }

    [Fact]
    public void BuilderPreservesBaseAndPromotionAndIsDeterministic()
    {
        var current = CurrentRevision();
        var listingId = Guid.Parse("0198fc00-0000-7000-8000-000000000020");
        var suppression = QueryVisibilitySuppression.Create(
            Guid.Parse("0198fc00-0000-7000-8000-000000000021"),
            current.CatalogKey,
            QueryVisibilitySuppressionTargetKind.Listing,
            listingId,
            listingId.ToString("D"),
            "legal-removal",
            QueryVisibilitySuppressionResponseMode.Gone,
            QueryVisibilitySuppressionState.Active,
            Timestamp,
            null,
            2,
            Timestamp);
        var overlayId = Guid.Parse("0198fc00-0000-7000-8000-000000000022");
        var publicReadId = Guid.Parse("0198fc00-0000-7000-8000-000000000023");

        var first = VisibilitySafetyProjectionBuilder.Build(
            current,
            new string('1', 64),
            new string('2', 64),
            4,
            [suppression],
            overlayId,
            publicReadId,
            Timestamp.AddMinutes(1));
        var second = VisibilitySafetyProjectionBuilder.Build(
            current,
            new string('1', 64),
            new string('2', 64),
            4,
            [suppression],
            overlayId,
            publicReadId,
            Timestamp.AddMinutes(1));

        Assert.Equal(first.Overlay.ContentDigest, second.Overlay.ContentDigest);
        Assert.Equal(first.PublicReadRevision.ContentDigest, second.PublicReadRevision.ContentDigest);
        Assert.Equal(current.BaseProjectionId, first.PublicReadRevision.BaseProjectionId);
        Assert.Equal(current.PromotionOverlayId, first.PublicReadRevision.PromotionOverlayId);
        Assert.Equal(overlayId, first.PublicReadRevision.SafetyOverlayId);
        Assert.Equal(1, first.Overlay.ItemCount);
    }

    private static PublicReadRevision CurrentRevision() =>
        PublicReadRevision.Restore(
            Guid.Parse("0198fc00-0000-7000-8000-000000000030"),
            "berlin-recording-services",
            Guid.Parse("0198fc00-0000-7000-8000-000000000031"),
            Guid.Parse("0198fc00-0000-7000-8000-000000000032"),
            Guid.Parse("0198fc00-0000-7000-8000-000000000033"),
            Guid.Parse("0198fc00-0000-7000-8000-000000000034"),
            Timestamp,
            new string('f', 64));

    private sealed class RecordingStore(PublicReadRevision result) :
        IVisibilitySafetyProjectionStore
    {
        public QueryVisibilitySuppression? Suppression { get; private set; }

        public VisibilitySuppressionInboxMessage? InboxMessage { get; private set; }

        public Task<VisibilitySafetyProjectionResult> ApplyAsync(
            QueryVisibilitySuppression suppression,
            VisibilitySuppressionInboxMessage inboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Suppression = suppression;
            InboxMessage = inboxMessage;
            return Task.FromResult(new VisibilitySafetyProjectionResult(
                result,
                VisibilitySafetyProjectionDisposition.Activated));
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }
}
