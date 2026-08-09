using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogListingDisputeServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid ListingId =
        Guid.Parse("0198ff21-0000-7000-8000-000000000001");
    private static readonly Guid DisputeId =
        Guid.Parse("0198ff21-0000-7000-8000-000000000002");
    private static readonly Guid ActorId =
        Guid.Parse("0198ff21-0000-7000-8000-000000000003");

    [Fact]
    public async Task OpenCreatesExactBlockingDisputeAgainstListingVersion()
    {
        var repository = new CapturingRepository();
        var service = CreateService(repository);

        var response = await service.OpenAsync(
            ListingId,
            new OpenCatalogListingDisputeRequest(
                ExpectedListingVersion: 7,
                Reason: "Provider contests the public listing facts."),
            CatalogActor.Create(ActorId),
            CatalogEventContext.Create("catalog-dispute-open"),
            CancellationToken.None);

        Assert.Equal(7, repository.ExpectedListingVersion);
        Assert.Equal("catalog-dispute-open", repository.EventContext?.CorrelationId);
        var dispute = Assert.IsType<ListingDispute>(repository.Added);
        Assert.Equal(DisputeId, dispute.Id);
        Assert.True(dispute.BlocksPromotion);
        Assert.Equal(ActorId, dispute.OpenedByActorId);
        Assert.Equal(Now, dispute.OpenedAtUtc);
        Assert.Equal(ListingDisputeStateContract.Open, response.State);
        Assert.True(response.BlocksPromotion);
        Assert.Equal(1, response.AggregateRevision);
    }

    [Fact]
    public async Task ResolveUsesExactStoredRevisionAndRetainsAudit()
    {
        var dispute = ListingDispute.Open(
            DisputeId,
            ListingId,
            "Provider contests the public listing facts.",
            ActorId,
            Now);
        var repository = new CapturingRepository { Existing = dispute };
        var service = CreateService(repository);
        var resolverId =
            Guid.Parse("0198ff21-0000-7000-8000-000000000004");

        var response = await service.ResolveAsync(
            ListingId,
            DisputeId,
            new ResolveCatalogListingDisputeRequest(
                ExpectedDisputeRevision: 1,
                ResolutionReason: "Evidence reviewed; dispute resolved."),
            CatalogActor.Create(resolverId),
            CatalogEventContext.Create("catalog-dispute-resolve"),
            CancellationToken.None);

        Assert.Equal(1, repository.ExpectedStoredAggregateRevision);
        Assert.Equal("catalog-dispute-resolve", repository.EventContext?.CorrelationId);
        Assert.Equal(ListingDisputeStateContract.Resolved, response.State);
        Assert.False(response.BlocksPromotion);
        Assert.Equal(resolverId, response.ResolvedByActorId);
        Assert.Equal(2, response.AggregateRevision);
        Assert.Equal("Provider contests the public listing facts.", response.OpenReason);
    }

    [Fact]
    public async Task UnknownDisputeFailsClosed()
    {
        var service = CreateService(new CapturingRepository());

        await Assert.ThrowsAsync<CatalogNotFoundException>(() =>
            service.ResolveAsync(
                ListingId,
                DisputeId,
                new ResolveCatalogListingDisputeRequest(
                    ExpectedDisputeRevision: 1,
                    ResolutionReason: "No matching dispute."),
                CatalogActor.Create(ActorId),
                CatalogEventContext.Create("catalog-dispute-missing"),
                CancellationToken.None));
    }

    private static CatalogListingDisputeService CreateService(
        ICatalogListingDisputeRepository repository) =>
        new(
            repository,
            new FixedIdSource(DisputeId),
            new FixedTimeProvider(Now));

    private sealed class FixedIdSource(Guid id) : ICatalogIdSource
    {
        public Guid CreateId() => id;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingRepository : ICatalogListingDisputeRepository
    {
        public ListingDispute? Added { get; private set; }

        public ListingDispute? Existing { get; init; }

        public long? ExpectedListingVersion { get; private set; }

        public long? ExpectedStoredAggregateRevision { get; private set; }

        public CatalogEventContext? EventContext { get; private set; }

        public Task<ListingDispute> AddAsync(
            ListingDispute dispute,
            long expectedListingVersion,
            CatalogEventContext eventContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Added = dispute;
            ExpectedListingVersion = expectedListingVersion;
            EventContext = eventContext;
            return Task.FromResult(dispute);
        }

        public Task<ListingDispute?> GetAsync(
            Guid listingId,
            Guid disputeId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Existing);
        }

        public Task<ListingDispute> SaveAsync(
            ListingDispute dispute,
            long expectedStoredAggregateRevision,
            CatalogEventContext eventContext,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExpectedStoredAggregateRevision = expectedStoredAggregateRevision;
            EventContext = eventContext;
            return Task.FromResult(dispute);
        }
    }
}
