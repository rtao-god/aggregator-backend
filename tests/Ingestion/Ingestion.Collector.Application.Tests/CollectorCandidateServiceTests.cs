using System.Security.Cryptography;
using System.Text;
using Aggregator.Ingestion.Collector.Application;
using Aggregator.Ingestion.Collector.Contracts;
using Aggregator.Ingestion.Collector.Domain;

namespace Ingestion.Collector.Application.Tests;

public sealed class CollectorCandidateServiceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidCandidateIsNormalizedAndRegisteredWithStableDigests()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest();

        var response = await service.SubmitAsync(request, CancellationToken.None);

        Assert.False(response.Replayed);
        Assert.Equal("acceptance-fixture", response.SourceSystem);
        Assert.Equal("https://example.test/studio", response.Website);
        Assert.Equal(64, response.ContentDigest.Length);
        Assert.NotNull(store.Candidate);
        Assert.Equal(response.CandidateId, store.Candidate.CandidateId);
        Assert.Equal(64, store.CommandDigest?.Length);
    }

    [Fact]
    public async Task NonUtcObservationFailsBeforePersistence()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest() with
        {
            ObservedAtUtc = new DateTimeOffset(2026, 8, 4, 8, 0, 0, TimeSpan.FromHours(2)),
        };

        var exception = await Assert.ThrowsAsync<CollectorCandidateException>(() =>
            service.SubmitAsync(request, CancellationToken.None));

        Assert.Equal("COLLECTOR_OBSERVATION_NOT_UTC", exception.Code);
        Assert.Null(store.Candidate);
    }

    [Fact]
    public async Task InvalidEvidenceDigestFailsClosed()
    {
        var store = new RecordingStore();
        var service = CreateService(store);
        var request = CreateRequest() with
        {
            EvidenceDigest = "not-a-digest",
        };

        var exception = await Assert.ThrowsAsync<CollectorCandidateException>(() =>
            service.SubmitAsync(request, CancellationToken.None));

        Assert.Equal("COLLECTOR_EVIDENCE_DIGEST_INVALID", exception.Code);
        Assert.Null(store.Candidate);
    }

    [Fact]
    public async Task StoreReplayIsPropagatedWithoutAllocatingDifferentResponseIdentity()
    {
        var store = new RecordingStore
        {
            Replay = true,
        };
        var service = CreateService(store);

        var response = await service.SubmitAsync(CreateRequest(), CancellationToken.None);

        Assert.True(response.Replayed);
        Assert.NotEqual(Guid.Empty, response.CandidateId);
        Assert.NotEqual(Guid.Empty, response.SubjectId);
    }

    private static CollectorCandidateService CreateService(RecordingStore store) =>
        new(
            store,
            new QueueIdSource(
                Guid.Parse("0198fa00-0000-7000-8000-000000000001"),
                Guid.Parse("0198fa00-0000-7000-8000-000000000002"),
                Guid.Parse("0198fa00-0000-7000-8000-000000000003")),
            new CollectorCandidateOptions(),
            new FixedTimeProvider(Now));

    private static SubmitCollectorCandidateRequest CreateRequest() =>
        new(
            Guid.Parse("0198fa00-0000-7000-8000-000000000010"),
            "Acceptance-Fixture",
            "https://collector.example/fixture/studio",
            Now.AddMinutes(-1),
            CollectorCandidateKindContract.Place,
            "studio-example",
            "Beispiel Tonstudio",
            "https://example.test/studio",
            80m,
            Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes("fixture"))));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class QueueIdSource(params Guid[] values) : ICollectorCandidateIdSource
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid CreateId() => _values.Dequeue();
    }

    private sealed class RecordingStore : ICollectorCandidateStore
    {
        public bool Replay { get; set; }

        public string? CommandDigest { get; private set; }

        public CollectorCandidate? Candidate { get; private set; }

        public Task<CollectorCandidateRegistration> RegisterAsync(
            Guid commandId,
            string commandDigest,
            CollectorCandidate candidate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEqual(Guid.Empty, commandId);
            CommandDigest = commandDigest;
            Candidate = candidate;
            return Task.FromResult(
                new CollectorCandidateRegistration(candidate, Replay));
        }

        public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(true);
        }
    }
}
