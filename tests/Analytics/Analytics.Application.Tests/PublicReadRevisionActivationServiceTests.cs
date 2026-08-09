using Aggregator.Analytics.Application;
using Aggregator.Query.Contracts;

namespace Analytics.Application.Tests;

public sealed class PublicReadRevisionActivationServiceTests
{
    private const string CanonicalMembershipDigest =
        "6d378433491a48895dee809af39ba3775e26220a2edec4dd6f097349df66c7ef";
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 7, 0, 0, TimeSpan.Zero);
    private static readonly Guid ListingId =
        Guid.Parse("0198a500-0000-7000-8000-000000000001");

    [Fact]
    public async Task CanonicalProducerActivationIsMappedToAtomicProjectionCommand()
    {
        var store = new CapturingProjectionStore();
        var service = new ApplyPublicReadRevisionActivationService(
            store,
            new FixedTimeProvider(Timestamp.AddSeconds(1)));
        var activation = CreateActivation(CanonicalMembershipDigest, [ListingId]);

        var result = await service.ApplyAsync(
            activation,
            new string('c', 64),
            "query-public-read-test",
            CancellationToken.None);

        Assert.Equal(PublicReadActivationDisposition.Applied, result.Disposition);
        var projection = Assert.IsType<PublicReadReferenceProjection>(store.Projection);
        Assert.Equal(activation.PublicReadRevisionId, projection.PublicReadRevisionId);
        Assert.Equal(activation.ActivationRevision, projection.ActivationRevision);
        Assert.Equal(activation.BaseProjectionId, projection.BaseProjectionId);
        Assert.Equal(activation.PromotionOverlayId, projection.PromotionOverlayId);
        Assert.Equal(activation.SafetyOverlayId, projection.SafetyOverlayId);
        Assert.Equal(activation.SourcePublicationId, projection.SourcePublicationId);
        Assert.Equal([ListingId], projection.PublicListingIds);
        Assert.Empty(projection.SponsoredPlacements);
        Assert.Equal(CanonicalMembershipDigest, projection.MembershipDigest);
        Assert.Equal(64, projection.ProjectionDigest.Length);

        var inbox = Assert.IsType<PublicReadActivationInboxMessage>(store.InboxMessage);
        Assert.Equal(activation.EventId, inbox.EventId);
        Assert.Equal(QueryIntegrationEventTypes.PublicReadRevisionActivated, inbox.RoutingKey);
        Assert.Equal(QueryIntegrationEventContracts.PublicReadRevisionActivated, inbox.ContractIdentity);
        Assert.Equal(activation.ActivationRevision, inbox.ActivationRevision);
        Assert.Equal("query-public-read-test", inbox.CorrelationId);
    }

    [Fact]
    public async Task MembershipDigestMismatchFailsBeforeProjectionPersistence()
    {
        var store = new CapturingProjectionStore();
        var service = new ApplyPublicReadRevisionActivationService(
            store,
            new FixedTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.ApplyAsync(
                CreateActivation(new string('d', 64), [ListingId]),
                new string('c', 64),
                "query-public-read-test",
                CancellationToken.None));

        Assert.Equal("ANALYTICS_PUBLIC_MEMBERSHIP_DIGEST_MISMATCH", exception.Code);
        Assert.Equal(422, exception.StatusCode);
        Assert.Null(store.Projection);
        Assert.Null(store.InboxMessage);
    }

    [Fact]
    public async Task NonCanonicalProducerMembershipOrderFailsClosed()
    {
        var firstListingId = Guid.Parse("0198a500-0000-7000-8000-000000000002");
        var secondListingId = Guid.Parse("0198a500-0000-7000-8000-000000000003");
        var store = new CapturingProjectionStore();
        var service = new ApplyPublicReadRevisionActivationService(
            store,
            new FixedTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.ApplyAsync(
                CreateActivation(
                    new string('e', 64),
                    [secondListingId, firstListingId]),
                new string('c', 64),
                "query-public-read-test",
                CancellationToken.None));

        Assert.Equal("ANALYTICS_PUBLIC_MEMBERSHIP_ORDER_INVALID", exception.Code);
        Assert.Null(store.Projection);
    }

    private static PublicReadRevisionActivated CreateActivation(
        string membershipDigest,
        IReadOnlyList<Guid> listingIds) =>
        new(
            Guid.Parse("0198a500-0000-7000-8000-000000000010"),
            Guid.Parse("0198a500-0000-7000-8000-000000000011"),
            "berlin-recording-services",
            1,
            Guid.Parse("0198a500-0000-7000-8000-000000000012"),
            Guid.Parse("0198a500-0000-7000-8000-000000000013"),
            Guid.Parse("0198a500-0000-7000-8000-000000000014"),
            Guid.Parse("0198a500-0000-7000-8000-000000000015"),
            new string('a', 64),
            membershipDigest,
            listingIds,
            Array.Empty<PublicReadSponsoredPlacementReference>(),
            Timestamp);

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class CapturingProjectionStore : IPublicReadActivationProjectionStore
    {
        public PublicReadReferenceProjection? Projection { get; private set; }

        public PublicReadActivationInboxMessage? InboxMessage { get; private set; }

        public Task<PublicReadActivationProjectionResult> ApplyAsync(
            PublicReadReferenceProjection projection,
            PublicReadActivationInboxMessage inboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Projection = projection;
            InboxMessage = inboxMessage;
            return Task.FromResult(new PublicReadActivationProjectionResult(
                projection,
                PublicReadActivationDisposition.Applied));
        }
    }
}
