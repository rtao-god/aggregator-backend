using Aggregator.Analytics.Application;
using Aggregator.Catalog.Contracts;

namespace Analytics.Application.Tests;

public sealed class ApplyCatalogListingAccessGrantChangedServiceTests
{
    private static readonly DateTimeOffset GrantedAtUtc =
        new(2026, 8, 9, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid MessageId =
        Guid.Parse("0198ff20-0000-7000-8000-000000000001");
    private static readonly Guid GrantId =
        Guid.Parse("0198ff20-0000-7000-8000-000000000002");
    private static readonly Guid ListingId =
        Guid.Parse("0198ff20-0000-7000-8000-000000000003");
    private static readonly Guid ActorId =
        Guid.Parse("0198ff20-0000-7000-8000-000000000004");

    [Fact]
    public async Task ActiveViewAnalyticsGrantCreatesAnAuthorizingProjection()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogListingAccessGrantChangedService(
            store,
            new FixedTimeProvider(GrantedAtUtc.AddSeconds(1)));

        var result = await service.ApplyAsync(
            CreateMessage(
                CatalogListingAccessGrantStateContract.Active,
                aggregateRevision: 1,
                occurredAtUtc: GrantedAtUtc,
                [
                    ListingAccessScopeContract.ReadDraft,
                    ListingAccessScopeContract.ViewAnalytics,
                    ListingAccessScopeContract.ManagePromotion,
                ]),
            CancellationToken.None);

        Assert.Equal(ListingMetricsAccessProjectionDisposition.Applied, result.Disposition);
        var change = Assert.IsType<ListingMetricsAccessProjectionChange>(store.Change);
        Assert.Equal(GrantId, change.Projection.GrantId);
        Assert.Equal(ListingId, change.Projection.ListingId);
        Assert.Equal(ActorId, change.Projection.ActorId);
        Assert.True(change.Projection.CanViewAnalytics);
        Assert.Null(change.Projection.RevokedAtUtc);
        Assert.Equal(1, change.Projection.SourceAggregateRevision);
        Assert.Matches("^[0-9a-f]{64}$", change.ProjectionDigest);
    }

    [Fact]
    public async Task RevokedGrantAlwaysRemovesAnalyticsAuthorization()
    {
        var revokedAtUtc = GrantedAtUtc.AddHours(1);
        var store = new CapturingStore();
        var service = new ApplyCatalogListingAccessGrantChangedService(
            store,
            new FixedTimeProvider(revokedAtUtc.AddSeconds(1)));

        await service.ApplyAsync(
            CreateMessage(
                CatalogListingAccessGrantStateContract.Revoked,
                aggregateRevision: 2,
                occurredAtUtc: revokedAtUtc,
                [ListingAccessScopeContract.ViewAnalytics]),
            CancellationToken.None);

        var projection = Assert.IsType<ListingMetricsAccessProjectionChange>(store.Change).Projection;
        Assert.False(projection.CanViewAnalytics);
        Assert.Equal(revokedAtUtc, projection.RevokedAtUtc);
        Assert.Equal(2, projection.SourceAggregateRevision);
    }

    [Fact]
    public async Task NonCanonicalPermissionsAreRejectedBeforePersistence()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogListingAccessGrantChangedService(
            store,
            new FixedTimeProvider(GrantedAtUtc.AddSeconds(1)));

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() => service.ApplyAsync(
            CreateMessage(
                CatalogListingAccessGrantStateContract.Active,
                aggregateRevision: 1,
                occurredAtUtc: GrantedAtUtc,
                [
                    ListingAccessScopeContract.ViewAnalytics,
                    ListingAccessScopeContract.ReadDraft,
                ]),
            CancellationToken.None));

        Assert.Equal("ANALYTICS_ACCESS_PERMISSIONS_NOT_CANONICAL", exception.Code);
        Assert.Null(store.Change);
    }

    [Fact]
    public async Task BrokerAndProducerEventIdentityMustMatch()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogListingAccessGrantChangedService(
            store,
            new FixedTimeProvider(GrantedAtUtc.AddSeconds(1)));
        var message = CreateMessage(
            CatalogListingAccessGrantStateContract.Active,
            aggregateRevision: 1,
            occurredAtUtc: GrantedAtUtc,
            [ListingAccessScopeContract.ViewAnalytics]) with
        {
            MessageId = Guid.Parse("0198ff20-0000-7000-8000-000000000099"),
        };

        var exception = await Assert.ThrowsAsync<AnalyticsCommandException>(() =>
            service.ApplyAsync(message, CancellationToken.None));

        Assert.Equal("ANALYTICS_ACCESS_MESSAGE_ID_MISMATCH", exception.Code);
        Assert.Null(store.Change);
    }

    private static CatalogListingAccessGrantProjectionMessage CreateMessage(
        CatalogListingAccessGrantStateContract state,
        long aggregateRevision,
        DateTimeOffset occurredAtUtc,
        IReadOnlyList<ListingAccessScopeContract> permissions) =>
        new(
            MessageId,
            CatalogIntegrationEventTypes.ListingAccessGrantChanged,
            CatalogIntegrationEventContracts.ListingAccessGrantChanged,
            new string('a', 64),
            "analytics-access-test",
            CausationId: null,
            new CatalogListingAccessGrantChanged(
                MessageId,
                GrantId,
                ListingId,
                ActorId,
                permissions,
                state,
                GrantedAtUtc,
                GrantedAtUtc.AddDays(30),
                aggregateRevision,
                occurredAtUtc));

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class CapturingStore : IListingMetricsAccessProjectionStore
    {
        public ListingMetricsAccessProjectionChange? Change { get; private set; }

        public Task<ListingMetricsAccessProjectionResult> ApplyAsync(
            ListingMetricsAccessProjectionChange change,
            DateTimeOffset receivedAtUtc,
            CancellationToken cancellationToken)
        {
            Change = change;
            return Task.FromResult(new ListingMetricsAccessProjectionResult(
                change.Projection,
                ListingMetricsAccessProjectionDisposition.Applied));
        }
    }
}
