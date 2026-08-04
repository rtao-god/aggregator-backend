using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Ingestion.Application.Tests;

public sealed class IngestionBatchReadTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 4, 6, 0, 0, TimeSpan.Zero);

    public static TheoryData<ImportBatchState, ImportBatchStateContract> StateMappings =>
        new()
        {
            { ImportBatchState.Registered, ImportBatchStateContract.Registered },
            { ImportBatchState.Uploading, ImportBatchStateContract.Uploading },
            { ImportBatchState.Uploaded, ImportBatchStateContract.Uploaded },
            { ImportBatchState.IntegrityChecking, ImportBatchStateContract.IntegrityChecking },
            { ImportBatchState.IntegrityValid, ImportBatchStateContract.IntegrityValid },
            { ImportBatchState.ItemValidation, ImportBatchStateContract.ItemValidation },
            { ImportBatchState.ReviewRequired, ImportBatchStateContract.ReviewRequired },
            { ImportBatchState.ReadyToCommit, ImportBatchStateContract.ReadyToCommit },
            { ImportBatchState.Committing, ImportBatchStateContract.Committing },
            { ImportBatchState.Committed, ImportBatchStateContract.Committed },
            { ImportBatchState.PartiallyRejected, ImportBatchStateContract.PartiallyRejected },
            { ImportBatchState.Superseded, ImportBatchStateContract.Superseded },
            { ImportBatchState.IntegrityFailed, ImportBatchStateContract.IntegrityFailed },
            { ImportBatchState.ContractRejected, ImportBatchStateContract.ContractRejected },
            { ImportBatchState.BlockedByPolicy, ImportBatchStateContract.BlockedByPolicy },
            { ImportBatchState.CommitFailed, ImportBatchStateContract.CommitFailed },
            { ImportBatchState.Expired, ImportBatchStateContract.Expired },
            { ImportBatchState.Cancelled, ImportBatchStateContract.Cancelled },
        };

    [Theory]
    [MemberData(nameof(StateMappings))]
    public void EveryDomainStateHasExactTransportMapping(
        ImportBatchState domainState,
        ImportBatchStateContract expectedContractState)
    {
        var snapshot = CreateSnapshot(domainState);

        var result = IngestionBatchContractMapper.ToDto(snapshot);

        Assert.Equal(expectedContractState, result.State);
        Assert.Equal(snapshot.Id.Value, result.Id);
        Assert.Equal(snapshot.PayloadDigest, result.PayloadDigest);
    }

    [Fact]
    public async Task ReadServiceReturnsNullOnlyForAbsentBatch()
    {
        var service = new ReadIngestionBatchService(new FixedRepository(null));

        var result = await service.ReadAsync(Guid.CreateVersion7(), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task ReadServiceRejectsEmptyBatchIdentity()
    {
        var service = new ReadIngestionBatchService(new FixedRepository(null));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.ReadAsync(Guid.Empty, CancellationToken.None));

        Assert.Equal("INGESTION_BATCH_ID_REQUIRED", exception.Code);
    }

    [Fact]
    public async Task ReadServiceReturnsExactPersistedSnapshot()
    {
        var snapshot = CreateSnapshot(ImportBatchState.ReviewRequired);
        var service = new ReadIngestionBatchService(new FixedRepository(snapshot));

        var result = await service.ReadAsync(snapshot.Id.Value, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(snapshot.Id.Value, result.Id);
        Assert.Equal(ImportBatchStateContract.ReviewRequired, result.State);
        Assert.Equal(snapshot.ReviewRequiredItemCount, result.ReviewRequiredItemCount);
    }

    private static IngestionBatchSnapshot CreateSnapshot(ImportBatchState state) =>
        new(
            ImportBatchId.Create(Guid.Parse("0198a123-0000-7000-8000-000000000201")),
            "collector-berlin",
            "build-2026-08-04",
            Guid.Parse("0198a123-0000-7000-8000-000000000202"),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            Guid.Parse("0198a123-0000-7000-8000-000000000203"),
            3,
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "ingestion/quarantine/package.json",
            new string('e', 64),
            4_096,
            "application/json",
            Timestamp,
            Timestamp.AddMinutes(1),
            state,
            4,
            1,
            2,
            0,
            null);

    private sealed class FixedRepository(IngestionBatchSnapshot? snapshot) : IIngestionBatchRepository
    {
        public Task<IngestionBatchRegistrationResult> RegisterAsync(
            ImportBatch batch,
            AggregatorCandidateIngestionManifest manifest,
            IngestionCommandIdentity commandIdentity,
            string callerServiceIdentity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IngestionBatchSnapshot?> ReadAsync(
            ImportBatchId batchId,
            CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}
