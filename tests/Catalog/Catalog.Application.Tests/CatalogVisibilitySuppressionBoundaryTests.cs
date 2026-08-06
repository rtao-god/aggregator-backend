using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogVisibilitySuppressionBoundaryTests
{
    [Fact]
    public async Task ExternalReferenceIdentityIsRejectedBeforePersistence()
    {
        var repository = new RejectUnexpectedRepositoryCall();
        var service = new CatalogVisibilitySuppressionService(
            repository,
            new QueueIdSource([Guid.Parse("0198fd00-0000-7000-8000-000000000001")]),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 6, 8, 30, 0, TimeSpan.Zero)));

        var exception = await Assert.ThrowsAsync<CatalogContractException>(() =>
            service.CreateActiveAsync(
                "berlin-recording-services",
                CreateRequest(
                    PublicVisibilitySuppressionTargetKindContract.ExternalReference,
                    Guid.Parse("0198fd00-0000-7000-8000-000000000002")),
                CatalogActor.Create(Guid.Parse("0198fd00-0000-7000-8000-000000000003")),
                CancellationToken.None));

        Assert.Equal("catalog.visibility_external_reference_identity_unsupported", exception.Code);
        Assert.False(repository.Called);
    }

    [Fact]
    public async Task ContactIdentityReachesCatalogPersistenceAndPublicEvent()
    {
        var contactId = Guid.Parse("0198fd00-0000-7000-8000-000000000010");
        var repository = new RecordingRepository(contactId);
        var service = new CatalogVisibilitySuppressionService(
            repository,
            new QueueIdSource(
            [
                Guid.Parse("0198fd00-0000-7000-8000-000000000011"),
                Guid.Parse("0198fd00-0000-7000-8000-000000000012"),
            ]),
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 6, 8, 30, 0, TimeSpan.Zero)));

        var response = await service.CreateActiveAsync(
            "berlin-recording-services",
            CreateRequest(PublicVisibilitySuppressionTargetKindContract.Contact, contactId),
            CatalogActor.Create(Guid.Parse("0198fd00-0000-7000-8000-000000000013")),
            CancellationToken.None);

        Assert.Equal(PublicVisibilitySuppressionStateContract.Active, response.State);
        Assert.Equal(PublicVisibilitySuppressionTargetKindContract.Contact, response.Target.Kind);
        Assert.Equal(contactId.ToString("D"), response.Target.TargetKey);
        Assert.NotNull(repository.Active);
        Assert.Equal(PublicVisibilitySuppressionTargetKind.Contact, repository.Active.Target.Kind);
        Assert.Equal(contactId.ToString("D"), repository.Active.Target.TargetKey);
        Assert.Equal(
            CatalogIntegrationEventContracts.PublicVisibilitySuppressionChanged,
            repository.OutboxMessage?.ContractIdentity);
    }

    private static CreatePublicVisibilitySuppressionRequest CreateRequest(
        PublicVisibilitySuppressionTargetKindContract targetKind,
        Guid targetId) =>
        new(
            new PublicVisibilitySuppressionTargetContract(
                targetKind,
                null,
                targetId.ToString("D")),
            "privacy-request",
            "catalog/privacy/private/request-002",
            PublicVisibilitySuppressionResponseModeContract.OmitChildElement,
            null,
            "Suppress one exact child identity.");

    private sealed class RejectUnexpectedRepositoryCall :
        ICatalogVisibilitySuppressionRepository
    {
        public bool Called { get; private set; }

        public Task EnsureTargetExistsAsync(
            CatalogKey catalogKey,
            PublicVisibilitySuppressionTarget target,
            CancellationToken cancellationToken)
        {
            Called = true;
            throw new InvalidOperationException("Persistence must not receive unsupported target identities.");
        }

        public Task<PublicVisibilitySuppression?> GetAsync(
            Guid suppressionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateActiveAsync(
            PublicVisibilitySuppression requested,
            PublicVisibilitySuppression active,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ResolveAsync(
            PublicVisibilitySuppression resolved,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingRepository(Guid expectedContactId) :
        ICatalogVisibilitySuppressionRepository
    {
        public PublicVisibilitySuppression? Active { get; private set; }

        public CatalogOutboxMessage? OutboxMessage { get; private set; }

        public Task EnsureTargetExistsAsync(
            CatalogKey catalogKey,
            PublicVisibilitySuppressionTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("berlin-recording-services", catalogKey.Value);
            Assert.Equal(PublicVisibilitySuppressionTargetKind.Contact, target.Kind);
            Assert.Equal(expectedContactId.ToString("D"), target.TargetKey);
            return Task.CompletedTask;
        }

        public Task<PublicVisibilitySuppression?> GetAsync(
            Guid suppressionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task CreateActiveAsync(
            PublicVisibilitySuppression requested,
            PublicVisibilitySuppression active,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(PublicVisibilitySuppressionState.Requested, requested.State);
            Active = active;
            OutboxMessage = outboxMessage;
            return Task.CompletedTask;
        }

        public Task ResolveAsync(
            PublicVisibilitySuppression resolved,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class QueueIdSource(IEnumerable<Guid> values) : ICatalogIdSource
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid CreateId() => _values.Dequeue();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
