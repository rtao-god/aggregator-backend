using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;

namespace Catalog.Application.Tests;

public sealed class CatalogPublicationOperationServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid OperationId = Guid.Parse("0198a111-0000-7000-8000-000000000001");
    private static readonly Guid PublicationId = Guid.Parse("0198a111-0000-7000-8000-000000000006");
    private static readonly Guid ActorId = Guid.Parse("0198a111-0000-7000-8000-000000000002");
    private static readonly Guid ConfigurationId = Guid.Parse("0198a111-0000-7000-8000-000000000003");
    private static readonly Guid ListingId = Guid.Parse("0198a111-0000-7000-8000-000000000004");
    private static readonly Guid ListingRevisionId = Guid.Parse("0198a111-0000-7000-8000-000000000005");

    [Fact]
    public async Task EnqueuePersistsOneCanonicalRequestSnapshot()
    {
        var store = new CapturingOperationStore();
        var service = new CatalogPublicationOperationService(
            store,
            new SequenceIdSource(OperationId, PublicationId),
            new FixedTimeProvider(Now));
        var request = CreateRequest();

        var response = await service.EnqueueAsync(
            request,
            CatalogActor.Create(ActorId),
            CatalogEventContext.Create("catalog-operation-test"),
            "catalog-publication-0001",
            CancellationToken.None);

        var registration = Assert.IsType<CatalogPublicationOperationRegistration>(store.Registration);
        Assert.Equal(OperationId, registration.OperationId);
        Assert.Equal(PublicationId, registration.PublicationId);
        Assert.Equal("catalog", registration.CatalogKey);
        Assert.Equal(ActorId, registration.ActorId);
        Assert.Equal("catalog-publication-0001", registration.IdempotencyKey);
        Assert.Equal("catalog-operation-test", registration.CorrelationId);
        Assert.Null(registration.CausationId);
        Assert.Equal(Now, registration.CreatedAtUtc);
        Assert.NotEmpty(registration.RequestDocument);
        Assert.Equal(64, registration.RequestDigest.Length);
        Assert.Equal(OperationId, response.OperationId);
        Assert.Equal(CatalogOperationStateContract.Pending, response.State);
        Assert.Equal(Now, response.CreatedAtUtc);
        Assert.Null(response.PublicationId);
        Assert.Null(response.Failure);
    }

    [Fact]
    public async Task DuplicateListingSelectionIsRejectedBeforePersistence()
    {
        var store = new CapturingOperationStore();
        var service = new CatalogPublicationOperationService(
            store,
            new SequenceIdSource(OperationId, PublicationId),
            new FixedTimeProvider(Now));
        var selection = new PublicationSelectionContract(ListingId, ListingRevisionId, 0);
        var request = new CreateCatalogPublicationRequest(
            "catalog",
            ConfigurationId,
            new PublicationPointerExpectationContract(PointerExpectationKindContract.Absent, null),
            [selection, selection]);

        var exception = await Assert.ThrowsAsync<CatalogContractException>(() => service.EnqueueAsync(
            request,
            CatalogActor.Create(ActorId),
            CatalogEventContext.Create("catalog-operation-test"),
            "catalog-publication-0002",
            CancellationToken.None));

        Assert.Equal("catalog.publication_duplicate_listing", exception.Code);
        Assert.Null(store.Registration);
    }

    [Fact]
    public async Task OperationReadDoesNotExposeAnotherActorsOperation()
    {
        var store = new CapturingOperationStore
        {
            Snapshot = new CatalogPublicationOperationSnapshot(
                OperationId,
                PublicationId,
                1,
                "catalog",
                ActorId,
                CatalogPublicationOperationState.Pending,
                0,
                Now,
                Now,
                null,
                null,
                null),
        };
        var service = new CatalogPublicationOperationService(
            store,
            new SequenceIdSource(OperationId, PublicationId),
            new FixedTimeProvider(Now));

        await Assert.ThrowsAsync<CatalogNotFoundException>(() => service.GetAsync(
            OperationId,
            CatalogActor.Create(Guid.Parse("0198a111-0000-7000-8000-000000000099")),
            CancellationToken.None));
    }

    private static CreateCatalogPublicationRequest CreateRequest() =>
        new(
            "catalog",
            ConfigurationId,
            new PublicationPointerExpectationContract(PointerExpectationKindContract.Absent, null),
            [new PublicationSelectionContract(ListingId, ListingRevisionId, 0)]);

    private sealed class SequenceIdSource(params Guid[] ids) : ICatalogIdSource
    {
        private readonly Queue<Guid> _ids = new(ids);

        public Guid CreateId() => _ids.Dequeue();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingOperationStore : ICatalogPublicationOperationStore
    {
        public CatalogPublicationOperationRegistration? Registration { get; private set; }

        public CatalogPublicationOperationSnapshot? Snapshot { get; init; }

        public Task<CatalogPublicationOperationSnapshot> RegisterAsync(
            CatalogPublicationOperationRegistration registration,
            CancellationToken cancellationToken)
        {
            Registration = registration;
            return Task.FromResult(new CatalogPublicationOperationSnapshot(
                registration.OperationId,
                registration.PublicationId,
                1,
                registration.CatalogKey,
                registration.ActorId,
                CatalogPublicationOperationState.Pending,
                0,
                registration.CreatedAtUtc,
                registration.CreatedAtUtc,
                null,
                null,
                null));
        }

        public Task<CatalogPublicationOperationSnapshot?> GetAsync(
            Guid operationId,
            CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);

        public Task<CatalogPublicationOperationLease?> ClaimNextAsync(
            string workerIdentity,
            DateTimeOffset claimedAtUtc,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ScheduleRetryAsync(
            Guid operationId,
            Guid leaseToken,
            CatalogPublicationOperationFailure failure,
            DateTimeOffset nextAttemptAtUtc,
            DateTimeOffset updatedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task FailAsync(
            Guid operationId,
            Guid leaseToken,
            CatalogPublicationOperationFailure failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
