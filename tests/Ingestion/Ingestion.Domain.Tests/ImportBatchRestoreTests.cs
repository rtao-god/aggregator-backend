using Aggregator.Ingestion.Domain;

namespace Ingestion.Domain.Tests;

public sealed class ImportBatchRestoreTests
{
    private static readonly DateTimeOffset RegisteredAt =
        new(2026, 8, 4, 6, 30, 0, TimeSpan.Zero);

    [Fact]
    public void UploadingSnapshotRestoresExactRevisionAndIdentity()
    {
        var batch = Restore(
            ImportBatchState.Uploading,
            aggregateRevision: 2,
            accepted: 0,
            review: 0,
            rejected: 0);

        Assert.Equal(ImportBatchState.Uploading, batch.State);
        Assert.Equal(2, batch.AggregateRevision);
        Assert.Equal("ingestion/quarantine/package.json", batch.PayloadObjectKey);
        Assert.Equal(RegisteredAt.AddMinutes(1), batch.LastChangedAtUtc);
    }

    [Fact]
    public void ReviewRequiredSnapshotMustCoverExactPackage()
    {
        var exception = Assert.Throws<IngestionDomainException>(() =>
            Restore(
                ImportBatchState.ReviewRequired,
                aggregateRevision: 7,
                accepted: 1,
                review: 1,
                rejected: 0,
                expectedItemCount: 3));

        Assert.Equal("INGESTION_DECISION_COUNTS_INVALID", exception.Code);
    }

    [Fact]
    public void FailureStateRequiresFailureCode()
    {
        var exception = Assert.Throws<IngestionDomainException>(() =>
            Restore(
                ImportBatchState.IntegrityFailed,
                aggregateRevision: 5,
                accepted: 0,
                review: 0,
                rejected: 0,
                failureCode: null));

        Assert.Equal("INGESTION_FAILURE_CODE_REQUIRED", exception.Code);
    }

    [Fact]
    public void NonFailureStateRejectsRetainedFailureCode()
    {
        var exception = Assert.Throws<IngestionDomainException>(() =>
            Restore(
                ImportBatchState.Uploaded,
                aggregateRevision: 3,
                accepted: 0,
                review: 0,
                rejected: 0,
                failureCode: "INGESTION_STALE_FAILURE"));

        Assert.Equal("INGESTION_FAILURE_CODE_INVALID", exception.Code);
    }

    [Fact]
    public void RestoredBatchContinuesThroughDomainTransition()
    {
        var batch = Restore(
            ImportBatchState.Uploading,
            aggregateRevision: 2,
            accepted: 0,
            review: 0,
            rejected: 0);

        batch.MarkUploaded(
            new string('e', 64),
            4_096,
            expectedAggregateRevision: 2,
            RegisteredAt.AddMinutes(2));

        Assert.Equal(ImportBatchState.Uploaded, batch.State);
        Assert.Equal(3, batch.AggregateRevision);
    }

    private static ImportBatch Restore(
        ImportBatchState state,
        long aggregateRevision,
        int accepted,
        int review,
        int rejected,
        int expectedItemCount = 2,
        string? failureCode = null) =>
        ImportBatch.Restore(
            ImportBatchId.Create(Guid.Parse("0198a123-0000-7000-8000-000000000501")),
            "collector-berlin",
            "build-2026-08-04",
            Guid.Parse("0198a123-0000-7000-8000-000000000502"),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            Guid.Parse("0198a123-0000-7000-8000-000000000503"),
            expectedItemCount,
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "ingestion/quarantine/package.json",
            new string('e', 64),
            4_096,
            "application/json",
            RegisteredAt,
            RegisteredAt.AddMinutes(1),
            state,
            aggregateRevision,
            accepted,
            review,
            rejected,
            failureCode);
}
