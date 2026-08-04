using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Ingestion.Application.Tests;

public sealed class IngestionUploadServiceTests
{
    private static readonly DateTimeOffset RegisteredAt =
        new(2026, 8, 4, 7, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PreparePersistsRegisteredToUploadingBeforeIssuingAuthorization()
    {
        var snapshot = CreateSnapshot(ImportBatchState.Registered, aggregateRevision: 1);
        var batchRepository = new FixedBatchRepository(snapshot);
        var lifecycleRepository = new RecordingLifecycleRepository();
        var payloadStore = new RecordingPayloadStore(() =>
        {
            Assert.Single(lifecycleRepository.SavedBatches);
            Assert.Equal(ImportBatchState.Uploading, lifecycleRepository.SavedBatches[0].State);
        });
        var service = new PrepareIngestionUploadService(
            batchRepository,
            lifecycleRepository,
            payloadStore,
            new FixedClock(RegisteredAt.AddMinutes(1)));

        var result = await service.PrepareAsync(
            new PrepareIngestionUploadCommand(
                snapshot.Id.Value,
                snapshot.AggregateRevision,
                "prepare-upload",
                "collector-service"),
            CancellationToken.None);

        Assert.False(result.Replayed);
        Assert.Equal(ImportBatchStateContract.Uploading, result.Batch.State);
        Assert.Equal(2, result.Batch.AggregateRevision);
        Assert.Equal(snapshot.PayloadObjectKey, result.ObjectKey);
        Assert.Equal(1, payloadStore.AuthorizationCount);
        Assert.Equal(1, batchRepository.ReadCount);
    }

    [Fact]
    public async Task PrepareReplayUsesExactCommandResultWithoutReloadingMutableBatch()
    {
        var resultSnapshot = CreateSnapshot(ImportBatchState.Uploading, aggregateRevision: 2);
        var batchRepository = new FixedBatchRepository(
            CreateSnapshot(ImportBatchState.Uploaded, aggregateRevision: 3));
        var lifecycleRepository = new RecordingLifecycleRepository
        {
            ReplayResult = resultSnapshot,
        };
        var payloadStore = new RecordingPayloadStore();
        var service = new PrepareIngestionUploadService(
            batchRepository,
            lifecycleRepository,
            payloadStore,
            new FixedClock(RegisteredAt.AddMinutes(2)));

        var result = await service.PrepareAsync(
            new PrepareIngestionUploadCommand(
                resultSnapshot.Id.Value,
                expectedAggregateRevision: 1,
                "prepare-upload",
                "collector-service"),
            CancellationToken.None);

        Assert.True(result.Replayed);
        Assert.Equal(ImportBatchStateContract.Uploading, result.Batch.State);
        Assert.Equal(0, batchRepository.ReadCount);
        Assert.Empty(lifecycleRepository.SavedBatches);
        Assert.Equal(1, payloadStore.AuthorizationCount);
    }

    [Fact]
    public async Task CompleteReplayDoesNotReverifyPayloadOrReadMutableBatch()
    {
        var resultSnapshot = CreateSnapshot(ImportBatchState.Uploaded, aggregateRevision: 3);
        var batchRepository = new FixedBatchRepository(
            CreateSnapshot(ImportBatchState.IntegrityChecking, aggregateRevision: 4));
        var lifecycleRepository = new RecordingLifecycleRepository
        {
            ReplayResult = resultSnapshot,
        };
        var payloadStore = new RecordingPayloadStore();
        var service = new CompleteIngestionUploadService(
            batchRepository,
            lifecycleRepository,
            payloadStore,
            new FixedClock(RegisteredAt.AddMinutes(3)));

        var result = await service.CompleteAsync(
            new CompleteIngestionUploadCommand(
                resultSnapshot.Id.Value,
                expectedAggregateRevision: 2,
                "complete-upload",
                "collector-service"),
            CancellationToken.None);

        Assert.True(result.Replayed);
        Assert.Equal(ImportBatchStateContract.Uploaded, result.Batch.State);
        Assert.Equal(0, batchRepository.ReadCount);
        Assert.Equal(0, payloadStore.VerificationCount);
        Assert.Empty(lifecycleRepository.SavedBatches);
    }

    [Fact]
    public async Task StaleCompleteRevisionFailsBeforeObjectStoreVerification()
    {
        var snapshot = CreateSnapshot(ImportBatchState.Uploading, aggregateRevision: 2);
        var batchRepository = new FixedBatchRepository(snapshot);
        var lifecycleRepository = new RecordingLifecycleRepository();
        var payloadStore = new RecordingPayloadStore();
        var service = new CompleteIngestionUploadService(
            batchRepository,
            lifecycleRepository,
            payloadStore,
            new FixedClock(RegisteredAt.AddMinutes(2)));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.CompleteAsync(
                new CompleteIngestionUploadCommand(
                    snapshot.Id.Value,
                    expectedAggregateRevision: 1,
                    "complete-upload",
                    "collector-service"),
                CancellationToken.None));

        Assert.Equal("INGESTION_BATCH_REVISION_CONFLICT", exception.Code);
        Assert.Equal(0, payloadStore.VerificationCount);
        Assert.Empty(lifecycleRepository.SavedBatches);
    }

    [Fact]
    public async Task DescriptorMismatchCannotAdvanceUploadingBatch()
    {
        var snapshot = CreateSnapshot(ImportBatchState.Uploading, aggregateRevision: 2);
        var batchRepository = new FixedBatchRepository(snapshot);
        var lifecycleRepository = new RecordingLifecycleRepository();
        var payloadStore = new RecordingPayloadStore
        {
            DescriptorOverride = new IngestionPayloadDescriptor(
                snapshot.PayloadObjectKey,
                new string('f', 64),
                snapshot.PayloadObjectSize,
                snapshot.PayloadContentType,
                RegisteredAt.AddMinutes(2)),
        };
        var service = new CompleteIngestionUploadService(
            batchRepository,
            lifecycleRepository,
            payloadStore,
            new FixedClock(RegisteredAt.AddMinutes(2)));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.CompleteAsync(
                new CompleteIngestionUploadCommand(
                    snapshot.Id.Value,
                    snapshot.AggregateRevision,
                    "complete-upload",
                    "collector-service"),
                CancellationToken.None));

        Assert.Equal("INGESTION_PAYLOAD_VERIFICATION_RESULT_INVALID", exception.Code);
        Assert.Equal(1, payloadStore.VerificationCount);
        Assert.Empty(lifecycleRepository.SavedBatches);
    }

    private static IngestionBatchSnapshot CreateSnapshot(
        ImportBatchState state,
        long aggregateRevision) =>
        new(
            ImportBatchId.Create(Guid.Parse("0198a123-0000-7000-8000-000000000601")),
            "collector-berlin",
            "build-2026-08-04",
            Guid.Parse("0198a123-0000-7000-8000-000000000602"),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            Guid.Parse("0198a123-0000-7000-8000-000000000603"),
            1,
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "ingestion/quarantine/package.json",
            new string('e', 64),
            4_096,
            "application/json",
            RegisteredAt,
            RegisteredAt.AddMinutes(aggregateRevision - 1),
            state,
            aggregateRevision,
            0,
            0,
            0,
            null);

    private sealed class FixedBatchRepository(IngestionBatchSnapshot? snapshot) : IIngestionBatchRepository
    {
        public int ReadCount { get; private set; }

        public Task<IngestionBatchRegistrationResult> RegisterAsync(
            ImportBatch batch,
            AggregatorCandidateIngestionManifest manifest,
            IngestionCommandIdentity commandIdentity,
            string callerServiceIdentity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IngestionBatchSnapshot?> ReadAsync(
            ImportBatchId batchId,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(snapshot);
        }
    }

    private sealed class RecordingLifecycleRepository : IIngestionBatchLifecycleRepository
    {
        public IngestionBatchSnapshot? ReplayResult { get; init; }

        public List<ImportBatch> SavedBatches { get; } = [];

        public Task<IngestionBatchSnapshot?> ReadCommandResultAsync(
            IngestionCommandIdentity commandIdentity,
            CancellationToken cancellationToken) =>
            Task.FromResult(ReplayResult);

        public Task<IngestionBatchCommandResult> SaveLifecycleAsync(
            ImportBatch batch,
            long expectedStoredAggregateRevision,
            IngestionCommandIdentity commandIdentity,
            string callerServiceIdentity,
            CancellationToken cancellationToken)
        {
            SavedBatches.Add(batch);
            return Task.FromResult(
                new IngestionBatchCommandResult(
                    IngestionBatchSnapshot.From(batch),
                    false));
        }
    }

    private sealed class RecordingPayloadStore(Action? beforeAuthorization = null) : IIngestionPayloadStore
    {
        public int AuthorizationCount { get; private set; }

        public int VerificationCount { get; private set; }

        public IngestionPayloadDescriptor? DescriptorOverride { get; init; }

        public Task<IngestionUploadAuthorization> CreateUploadAuthorizationAsync(
            string objectKey,
            string contentType,
            long maximumSize,
            TimeSpan lifetime,
            CancellationToken cancellationToken)
        {
            beforeAuthorization?.Invoke();
            AuthorizationCount++;
            return Task.FromResult(
                new IngestionUploadAuthorization(
                    new Uri("https://object-store.test/upload", UriKind.Absolute),
                    objectKey,
                    RegisteredAt.Add(lifetime),
                    contentType,
                    maximumSize));
        }

        public Task<IngestionPayloadDescriptor> VerifyUploadedAsync(
            string objectKey,
            string expectedContentDigest,
            long expectedSize,
            string expectedContentType,
            CancellationToken cancellationToken)
        {
            VerificationCount++;
            return Task.FromResult(
                DescriptorOverride ?? new IngestionPayloadDescriptor(
                    objectKey,
                    expectedContentDigest,
                    expectedSize,
                    expectedContentType,
                    RegisteredAt.AddMinutes(2)));
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IIngestionClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }
}
