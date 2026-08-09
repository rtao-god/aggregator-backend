using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Application.Tests;

public sealed class IngestionProducerRegistrationServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task CreateBuildsOneCanonicalRevision()
    {
        var store = new CapturingStore();
        var service = new IngestionProducerRegistrationService(
            store,
            new FixedClock(Now));

        var result = await service.PutAsync(
            new PutIngestionProducerRegistrationCommand(
                "collector.berlin",
                0,
                true,
                [AggregatorCandidateIngestionContract.Revision],
                "Authorize the Berlin collector workload.",
                "producer-registration-0001",
                "platform-admin"),
            CancellationToken.None);

        var mutation = Assert.IsType<IngestionProducerRegistrationMutation>(store.Mutation);
        Assert.False(result.Replayed);
        Assert.Equal("collector.berlin", mutation.Registration.ProducerIdentity);
        Assert.Equal(1, mutation.Registration.AggregateRevision);
        Assert.True(mutation.Registration.Active);
        Assert.Equal([1], mutation.Registration.SupportedContractRevisions);
        Assert.Equal("platform-admin", mutation.Registration.UpdatedByServiceIdentity);
        Assert.Equal(Now, mutation.Registration.UpdatedAtUtc);
        Assert.Equal(64, mutation.Registration.ContentDigest.Length);
        Assert.Equal("producer-registration", mutation.CommandIdentity.Scope);
        Assert.Equal("producer-registration-0001", mutation.CommandIdentity.Key);
        Assert.Equal(64, mutation.CommandIdentity.RequestDigest.Length);
    }

    [Fact]
    public async Task DuplicateOrUnsupportedContractRevisionIsRejected()
    {
        var store = new CapturingStore();
        var service = new IngestionProducerRegistrationService(
            store,
            new FixedClock(Now));

        var duplicate = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.PutAsync(
                CreateCommand([1, 1]),
                CancellationToken.None));
        Assert.Equal("INGESTION_PRODUCER_REVISION_UNSUPPORTED", duplicate.Code);

        var unsupported = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.PutAsync(
                CreateCommand([2]),
                CancellationToken.None));
        Assert.Equal("INGESTION_PRODUCER_REVISION_UNSUPPORTED", unsupported.Code);
        Assert.Null(store.Mutation);
    }

    [Fact]
    public async Task NegativeExpectedRevisionIsRejectedBeforePersistence()
    {
        var store = new CapturingStore();
        var service = new IngestionProducerRegistrationService(
            store,
            new FixedClock(Now));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.PutAsync(
                CreateCommand([1]) with { ExpectedAggregateRevision = -1 },
                CancellationToken.None));

        Assert.Equal("INGESTION_PRODUCER_EXPECTED_REVISION_INVALID", exception.Code);
        Assert.Null(store.Mutation);
    }

    [Fact]
    public void RevisionDigestChangesWithAggregateRevision()
    {
        var first = IngestionProducerRegistrationService.ComputeContentDigest(
            "collector.berlin",
            true,
            [1],
            1);
        var second = IngestionProducerRegistrationService.ComputeContentDigest(
            "collector.berlin",
            true,
            [1],
            2);

        Assert.NotEqual(first, second);
        Assert.Equal(64, first.Length);
        Assert.Equal(64, second.Length);
    }

    [Fact]
    public async Task ReadUsesTheSameCanonicalStore()
    {
        var snapshot = new IngestionProducerRegistrationSnapshot(
            "collector.berlin",
            true,
            [1],
            3,
            IngestionProducerRegistrationService.ComputeContentDigest(
                "collector.berlin",
                true,
                [1],
                3),
            "platform-admin",
            "Retain the active collector registration.",
            Now);
        var store = new CapturingStore { Snapshot = snapshot };
        var service = new IngestionProducerRegistrationService(
            store,
            new FixedClock(Now));

        var actual = await service.ReadAsync(
            "collector.berlin",
            CancellationToken.None);

        Assert.Same(snapshot, actual);
        Assert.Equal("collector.berlin", store.ReadIdentity);
    }

    private static PutIngestionProducerRegistrationCommand CreateCommand(
        IReadOnlyList<int> revisions) =>
        new(
            "collector.berlin",
            0,
            true,
            revisions,
            "Authorize the Berlin collector workload.",
            "producer-registration-0001",
            "platform-admin");

    private sealed class FixedClock(DateTimeOffset now) : IIngestionClock
    {
        public DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CapturingStore : IIngestionProducerRegistrationStore
    {
        public IngestionProducerRegistrationMutation? Mutation { get; private set; }

        public IngestionProducerRegistrationSnapshot? Snapshot { get; init; }

        public string? ReadIdentity { get; private set; }

        public Task<IngestionProducerRegistrationMutationResult> PutAsync(
            IngestionProducerRegistrationMutation mutation,
            CancellationToken cancellationToken)
        {
            Mutation = mutation;
            return Task.FromResult(new IngestionProducerRegistrationMutationResult(
                mutation.Registration,
                Replayed: false));
        }

        public Task<IngestionProducerRegistrationSnapshot?> ReadAsync(
            string producerIdentity,
            CancellationToken cancellationToken)
        {
            ReadIdentity = producerIdentity;
            return Task.FromResult(Snapshot);
        }
    }
}
