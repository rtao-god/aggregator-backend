using Aggregator.Ingestion.Domain;

namespace Ingestion.Domain.Tests;

public sealed class ImportBatchTests
{
    private static readonly DateTimeOffset RegisteredAt =
        new(2026, 8, 4, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IntegrityFailureIsTerminalForOrdinaryProcessing()
    {
        var batch = CreateBatch(itemCount: 1);
        batch.BeginUpload(batch.AggregateRevision, RegisteredAt.AddMinutes(1));
        batch.MarkUploaded(
            new string('e', 64),
            1_024,
            batch.AggregateRevision,
            RegisteredAt.AddMinutes(2));
        batch.BeginIntegrityCheck(batch.AggregateRevision, RegisteredAt.AddMinutes(3));
        batch.RejectIntegrity(
            "INGESTION_PAYLOAD_DIGEST_MISMATCH",
            batch.AggregateRevision,
            RegisteredAt.AddMinutes(4));

        var exception = Assert.Throws<IngestionDomainException>(() =>
            batch.BeginItemValidation(batch.AggregateRevision, RegisteredAt.AddMinutes(5)));

        Assert.Equal("INGESTION_BATCH_STATE_INVALID", exception.Code);
        Assert.Equal(ImportBatchState.IntegrityFailed, batch.State);
    }

    [Fact]
    public void ItemDecisionCountsMustCoverExactRegisteredPackage()
    {
        var batch = MoveToItemValidation(CreateBatch(itemCount: 3));

        var exception = Assert.Throws<IngestionDomainException>(() =>
            batch.CompleteItemValidation(
                acceptedItemCount: 1,
                reviewRequiredItemCount: 0,
                rejectedItemCount: 1,
                batch.AggregateRevision,
                RegisteredAt.AddMinutes(6)));

        Assert.Equal("INGESTION_DECISION_COUNTS_INVALID", exception.Code);
        Assert.Equal(ImportBatchState.ItemValidation, batch.State);
    }

    [Fact]
    public void RejectedCatalogOutcomesProduceExplicitPartialState()
    {
        var batch = MoveToItemValidation(CreateBatch(itemCount: 2));
        batch.CompleteItemValidation(
            acceptedItemCount: 2,
            reviewRequiredItemCount: 0,
            rejectedItemCount: 0,
            batch.AggregateRevision,
            RegisteredAt.AddMinutes(6));
        batch.BeginCommit(batch.AggregateRevision, RegisteredAt.AddMinutes(7));
        batch.CompleteCommit(
            deliveredItemCount: 1,
            rejectedItemCount: 1,
            batch.AggregateRevision,
            RegisteredAt.AddMinutes(8));

        Assert.Equal(ImportBatchState.PartiallyRejected, batch.State);
        Assert.Equal(1, batch.AcceptedItemCount);
        Assert.Equal(1, batch.RejectedItemCount);
    }

    [Fact]
    public void LaterItemDecisionRequiresExactSupersessionIdentity()
    {
        var exception = Assert.Throws<IngestionDomainException>(() =>
            ImportItemDecision.Create(
                Guid.CreateVersion7(),
                ImportBatchId.Create(Guid.CreateVersion7()),
                "candidate:42",
                sequence: 2,
                ImportItemDecisionKind.Accepted,
                "INGESTION_REVIEW_ACCEPTED",
                IngestionActorId.Create(Guid.CreateVersion7()),
                RegisteredAt));

        Assert.Equal("INGESTION_DECISION_SUPERSESSION_REQUIRED", exception.Code);
    }

    [Fact]
    public void StaleAggregateRevisionCannotMutateBatch()
    {
        var batch = CreateBatch(itemCount: 1);

        var exception = Assert.Throws<IngestionDomainException>(() =>
            batch.BeginUpload(expectedAggregateRevision: 0, RegisteredAt.AddMinutes(1)));

        Assert.Equal("INGESTION_BATCH_REVISION_CONFLICT", exception.Code);
        Assert.Equal(ImportBatchState.Registered, batch.State);
    }

    private static ImportBatch MoveToItemValidation(ImportBatch batch)
    {
        batch.BeginUpload(batch.AggregateRevision, RegisteredAt.AddMinutes(1));
        batch.MarkUploaded(
            new string('e', 64),
            1_024,
            batch.AggregateRevision,
            RegisteredAt.AddMinutes(2));
        batch.BeginIntegrityCheck(batch.AggregateRevision, RegisteredAt.AddMinutes(3));
        batch.MarkIntegrityValid(batch.AggregateRevision, RegisteredAt.AddMinutes(4));
        batch.BeginItemValidation(batch.AggregateRevision, RegisteredAt.AddMinutes(5));
        return batch;
    }

    private static ImportBatch CreateBatch(int itemCount) =>
        ImportBatch.Create(
            ImportBatchId.Create(Guid.CreateVersion7()),
            "collector-berlin",
            "build-2026-08-04",
            Guid.CreateVersion7(),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            Guid.CreateVersion7(),
            itemCount,
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "ingestion/quarantine/package.json",
            new string('e', 64),
            1_024,
            "application/json",
            RegisteredAt);
}
