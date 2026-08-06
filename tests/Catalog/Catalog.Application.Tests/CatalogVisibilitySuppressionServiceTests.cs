using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogVisibilitySuppressionServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 6, 7, 0, 0, TimeSpan.Zero);

    private static readonly Guid ListingId =
        Guid.Parse("0198fb00-0000-7000-8000-000000000001");

    private static readonly Guid SuppressionId =
        Guid.Parse("0198fb00-0000-7000-8000-000000000002");

    private static readonly Guid CreateEventId =
        Guid.Parse("0198fb00-0000-7000-8000-000000000003");

    private static readonly Guid ResolveEventId =
        Guid.Parse("0198fb00-0000-7000-8000-000000000004");

    private static readonly Guid ActorId =
        Guid.Parse("0198fb00-0000-7000-8000-000000000005");

    [Fact]
    public async Task CreateAndResolvePersistExactRevisionsWithMinimalPublicEvents()
    {
        var repository = new RecordingSuppressionRepository();
        var service = new CatalogVisibilitySuppressionService(
            repository,
            new QueueIdSource([SuppressionId, CreateEventId, ResolveEventId]),
            new FixedTimeProvider(Timestamp));
        var actor = CatalogActor.Create(ActorId);
        var context = CatalogEventContext.Create(
            "catalog-visibility-test",
            Guid.Parse("0198fb00-0000-7000-8000-000000000006"));

        var active = await service.CreateActiveAsync(
            "berlin-recording-services",
            new CreatePublicVisibilitySuppressionRequest(
                new PublicVisibilitySuppressionTargetContract(
                    PublicVisibilitySuppressionTargetKindContract.Listing,
                    ListingId,
                    ListingId.ToString("D")),
                "legal-removal",
                "catalog/claims/private/evidence-001",
                PublicVisibilitySuppressionResponseModeContract.Gone,
                Timestamp.AddDays(7),
                "Hide the exact listing while replacement publication is prepared."),
            actor,
            context,
            CancellationToken.None);

        Assert.Equal(PublicVisibilitySuppressionStateContract.Active, active.State);
        Assert.Equal(2, active.Revision);
        Assert.Equal([1L, 2L], repository.Revisions.Select(item => item.Revision));
        var createMessage = Assert.Single(repository.OutboxMessages);
        Assert.Equal(CatalogIntegrationEventTypes.PublicVisibilitySuppressionChanged, createMessage.EventType);
        Assert.Equal(CatalogIntegrationEventContracts.PublicVisibilitySuppressionChanged, createMessage.ContractIdentity);
        Assert.Equal(context.CorrelationId, createMessage.CorrelationId);
        Assert.Equal(context.CausationId, createMessage.CausationId);
        Assert.DoesNotContain("privateEvidenceReference", createMessage.Payload);
        Assert.DoesNotContain("evidence-001", createMessage.Payload);
        Assert.Contains(SuppressionId.ToString("D"), createMessage.Payload);

        var resolved = await service.ResolveAsync(
            "berlin-recording-services",
            SuppressionId,
            new ResolvePublicVisibilitySuppressionRequest(
                2,
                "Replacement publication no longer contains the listing."),
            actor,
            context,
            CancellationToken.None);

        Assert.Equal(PublicVisibilitySuppressionStateContract.Resolved, resolved.State);
        Assert.Equal(3, resolved.Revision);
        Assert.Equal([1L, 2L, 3L], repository.Revisions.Select(item => item.Revision));
        Assert.Equal(2, repository.OutboxMessages.Count);
        Assert.All(
            repository.OutboxMessages,
            message => Assert.DoesNotContain("privateEvidenceReference", message.Payload));
    }

    [Fact]
    public async Task ResolveRejectsStaleRevisionBeforePersistence()
    {
        var repository = new RecordingSuppressionRepository();
        var service = new CatalogVisibilitySuppressionService(
            repository,
            new QueueIdSource([SuppressionId, CreateEventId]),
            new FixedTimeProvider(Timestamp));
        var actor = CatalogActor.Create(ActorId);

        _ = await service.CreateActiveAsync(
            "berlin-recording-services",
            new CreatePublicVisibilitySuppressionRequest(
                new PublicVisibilitySuppressionTargetContract(
                    PublicVisibilitySuppressionTargetKindContract.Listing,
                    ListingId,
                    ListingId.ToString("D")),
                "privacy-request",
                "catalog/privacy/private/request-001",
                PublicVisibilitySuppressionResponseModeContract.HideAsNotFound,
                null,
                "Hide the exact listing."),
            actor,
            CancellationToken.None);

        var exception = await Assert.ThrowsAsync<CatalogSuppressionConcurrencyException>(() =>
            service.ResolveAsync(
                "berlin-recording-services",
                SuppressionId,
                new ResolvePublicVisibilitySuppressionRequest(
                    1,
                    "Stale resolve command."),
                actor,
                CancellationToken.None));

        Assert.Equal(1, exception.ExpectedRevision);
        Assert.Equal(2, exception.ActualRevision);
        Assert.Equal([1L, 2L], repository.Revisions.Select(item => item.Revision));
        Assert.Single(repository.OutboxMessages);
    }

    private sealed class RecordingSuppressionRepository :
        ICatalogVisibilitySuppressionRepository
    {
        private PublicVisibilitySuppression? _current;

        public List<PublicVisibilitySuppression> Revisions { get; } = [];

        public List<CatalogOutboxMessage> OutboxMessages { get; } = [];

        public Task EnsureTargetExistsAsync(
            CatalogKey catalogKey,
            PublicVisibilitySuppressionTarget target,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("berlin-recording-services", catalogKey.Value);
            Assert.Equal(PublicVisibilitySuppressionTargetKind.Listing, target.Kind);
            Assert.Equal(ListingId, target.ListingId);
            return Task.CompletedTask;
        }

        public Task<PublicVisibilitySuppression?> GetAsync(
            Guid suppressionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _current?.Id == suppressionId
                    ? _current
                    : null);
        }

        public Task CreateActiveAsync(
            PublicVisibilitySuppression requested,
            PublicVisibilitySuppression active,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Null(_current);
            Revisions.Add(requested);
            Revisions.Add(active);
            _current = active;
            OutboxMessages.Add(outboxMessage);
            return Task.CompletedTask;
        }

        public Task ResolveAsync(
            PublicVisibilitySuppression resolved,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(_current);
            Assert.Equal(checked(_current.Revision + 1), resolved.Revision);
            Revisions.Add(resolved);
            _current = resolved;
            OutboxMessages.Add(outboxMessage);
            return Task.CompletedTask;
        }
    }

    private sealed class QueueIdSource(IEnumerable<Guid> values) : ICatalogIdSource
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid CreateId() =>
            _values.Count > 0
                ? _values.Dequeue()
                : throw new InvalidOperationException("The test ID sequence is exhausted.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
