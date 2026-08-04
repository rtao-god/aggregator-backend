using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Ingestion.Application.Tests;

public sealed class IngestionReviewCommitWorkflowTests
{
    [Fact]
    public async Task ReviewSelectionAndCatalogOutcomesCoverExactRegisteredPackage()
    {
        var batch = CreateReviewRequiredBatch();
        var storage = new WorkflowStorage(IngestionBatchSnapshot.From(batch));
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 4, 8, 0, 0, TimeSpan.Zero));
        var review = new CompleteIngestionReviewService(storage, storage, clock);
        var beginCommit = new BeginIngestionCommitService(storage, storage, clock);
        var completeCommit = new CompleteIngestionCommitService(storage, storage, clock);

        var reviewResult = await review.CompleteAsync(
            new CompleteIngestionReviewCommand(
                batch.Id.Value,
                batch.AggregateRevision,
                [
                    new IngestionReviewResolution(
                        "item-review",
                        IngestionItemDecisionContract.Accepted,
                        ["operator.confirmed"]),
                ],
                "review-key",
                "operator:reviewer"),
            CancellationToken.None);

        Assert.False(reviewResult.Replayed);
        Assert.Equal(ImportBatchState.ReadyToCommit, reviewResult.Batch.State);
        Assert.Equal(2, reviewResult.Batch.AcceptedItemCount);
        Assert.Equal(0, reviewResult.Batch.ReviewRequiredItemCount);
        Assert.Equal(1, reviewResult.Batch.RejectedItemCount);

        var beginResult = await beginCommit.BeginAsync(
            new BeginIngestionCommitCommand(
                batch.Id.Value,
                reviewResult.Batch.AggregateRevision,
                ["item-accepted", "item-review"],
                "begin-key",
                "worker:catalog-delivery"),
            CancellationToken.None);

        Assert.Equal(ImportBatchState.Committing, beginResult.Batch.State);

        var completed = await completeCommit.CompleteAsync(
            new CompleteIngestionCommitCommand(
                batch.Id.Value,
                beginResult.Batch.AggregateRevision,
                [
                    new IngestionCatalogDeliveryOutcome(
                        "item-accepted",
                        Guid.Parse("0198a123-1000-7000-8000-000000000001"),
                        IngestionCatalogDeliveryOutcomeContract.Delivered,
                        Guid.Parse("0198a123-1000-7000-8000-000000000002"),
                        Guid.Parse("0198a123-1000-7000-8000-000000000003"),
                        Guid.Parse("0198a123-1000-7000-8000-000000000004"),
                        FailureCode: null),
                    new IngestionCatalogDeliveryOutcome(
                        "item-review",
                        Guid.Parse("0198a123-1000-7000-8000-000000000005"),
                        IngestionCatalogDeliveryOutcomeContract.Rejected,
                        CatalogSubjectId: null,
                        CatalogListingId: null,
                        CatalogListingRevisionId: null,
                        "catalog.validation_failed"),
                ],
                "complete-key",
                "worker:catalog-delivery"),
            CancellationToken.None);

        Assert.False(completed.Replayed);
        Assert.Equal(ImportBatchState.PartiallyRejected, completed.Batch.State);
        Assert.Equal(1, completed.Batch.AcceptedItemCount);
        Assert.Equal(0, completed.Batch.ReviewRequiredItemCount);
        Assert.Equal(2, completed.Batch.RejectedItemCount);
        Assert.Equal(3, completed.Batch.AcceptedItemCount + completed.Batch.RejectedItemCount);
    }

    [Fact]
    public async Task ExactReviewReplayReturnsImmutablePriorResult()
    {
        var batch = CreateReviewRequiredBatch();
        var storage = new WorkflowStorage(IngestionBatchSnapshot.From(batch));
        var service = new CompleteIngestionReviewService(
            storage,
            storage,
            new FixedClock(new DateTimeOffset(2026, 8, 4, 8, 0, 0, TimeSpan.Zero)));
        var command = new CompleteIngestionReviewCommand(
            batch.Id.Value,
            batch.AggregateRevision,
            [
                new IngestionReviewResolution(
                    "item-review",
                    IngestionItemDecisionContract.Rejected,
                    ["operator.rejected"]),
            ],
            "review-replay-key",
            "operator:reviewer");

        var first = await service.CompleteAsync(command, CancellationToken.None);
        storage.AdvanceCurrentProjectionForProof();
        var replay = await service.CompleteAsync(command, CancellationToken.None);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Batch, replay.Batch);
        Assert.NotEqual(storage.Current.State, replay.Batch.State);
    }

    [Fact]
    public async Task CommitCompletionRejectsOutcomeCoverageDrift()
    {
        var batch = CreateReviewRequiredBatch();
        batch.CompleteReview(2, 1, batch.AggregateRevision, batch.LastChangedAtUtc);
        batch.BeginCommit(batch.AggregateRevision, batch.LastChangedAtUtc);
        var storage = new WorkflowStorage(IngestionBatchSnapshot.From(batch));
        var service = new CompleteIngestionCommitService(
            storage,
            storage,
            new FixedClock(batch.LastChangedAtUtc));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.CompleteAsync(
                new CompleteIngestionCommitCommand(
                    batch.Id.Value,
                    batch.AggregateRevision,
                    [
                        new IngestionCatalogDeliveryOutcome(
                            "only-one-item",
                            Guid.Parse("0198a123-2000-7000-8000-000000000001"),
                            IngestionCatalogDeliveryOutcomeContract.Rejected,
                            null,
                            null,
                            null,
                            "catalog.rejected"),
                    ],
                    "coverage-key",
                    "worker:catalog-delivery"),
                CancellationToken.None));

        Assert.Equal("INGESTION_CATALOG_OUTCOME_COVERAGE_INVALID", exception.Code);
    }

    private static ImportBatch CreateReviewRequiredBatch()
    {
        var timestamp = new DateTimeOffset(2026, 8, 4, 7, 0, 0, TimeSpan.Zero);
        var batch = ImportBatch.Create(
            ImportBatchId.Create(Guid.Parse("0198a123-0000-7000-8000-000000000101")),
            "collector-berlin",
            "collector-build-1",
            Guid.Parse("0198a123-0000-7000-8000-000000000102"),
            new string('a', 64),
            "berlin",
            "providers",
            Guid.Parse("0198a123-0000-7000-8000-000000000103"),
            expectedItemCount: 3,
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "ingestion/quarantine/package.json",
            new string('e', 64),
            payloadObjectSize: 1024,
            "application/json",
            timestamp);
        batch.BeginUpload(batch.AggregateRevision, timestamp);
        batch.MarkUploaded(new string('e', 64), 1024, batch.AggregateRevision, timestamp);
        batch.BeginIntegrityCheck(batch.AggregateRevision, timestamp);
        batch.MarkIntegrityValid(batch.AggregateRevision, timestamp);
        batch.BeginItemValidation(batch.AggregateRevision, timestamp);
        batch.CompleteItemValidation(
            acceptedItemCount: 1,
            reviewRequiredItemCount: 1,
            rejectedItemCount: 1,
            batch.AggregateRevision,
            timestamp);
        return batch;
    }

    private sealed class FixedClock(DateTimeOffset value) : IIngestionClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class WorkflowStorage :
        IIngestionBatchRepository,
        IIngestionReviewCommitRepository
    {
        private readonly Dictionary<(string Scope, string Key), (string Digest, IngestionBatchSnapshot Result)> _results = [];

        public WorkflowStorage(IngestionBatchSnapshot current)
        {
            Current = current;
        }

        public IngestionBatchSnapshot Current { get; private set; }

        public Task<IngestionBatchRegistrationResult> RegisterAsync(
            ImportBatch batch,
            AggregatorCandidateIngestionManifest manifest,
            IngestionCommandIdentity commandIdentity,
            string callerServiceIdentity,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Registration is outside this workflow proof.");

        public Task<IngestionBatchSnapshot?> ReadAsync(
            ImportBatchId batchId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IngestionBatchSnapshot?>(
                batchId.Value == Current.Id.Value ? Current : null);

        public Task<IngestionBatchSnapshot?> ReadCommandResultAsync(
            IngestionCommandIdentity commandIdentity,
            CancellationToken cancellationToken)
        {
            if (!_results.TryGetValue((commandIdentity.Scope, commandIdentity.Key), out var value))
            {
                return Task.FromResult<IngestionBatchSnapshot?>(null);
            }

            if (!string.Equals(value.Digest, commandIdentity.RequestDigest, StringComparison.Ordinal))
            {
                throw new IngestionApplicationException(
                    "Ingestion.Commands",
                    "INGESTION_IDEMPOTENCY_DIGEST_CONFLICT",
                    409,
                    "The key belongs to another request.",
                    "Use the exact request or another key.");
            }

            return Task.FromResult<IngestionBatchSnapshot?>(value.Result);
        }

        public Task<IngestionBatchCommandResult> CompleteReviewAsync(
            ImportBatch batch,
            long expectedStoredAggregateRevision,
            IReadOnlyList<IngestionReviewResolution> resolutions,
            IngestionCommandIdentity commandIdentity,
            string callerIdentity,
            CancellationToken cancellationToken) =>
            Save(batch, commandIdentity);

        public Task<IngestionBatchCommandResult> BeginCommitAsync(
            ImportBatch batch,
            long expectedStoredAggregateRevision,
            IReadOnlyList<string> selectedItemKeys,
            IngestionCommandIdentity commandIdentity,
            string callerIdentity,
            CancellationToken cancellationToken) =>
            Save(batch, commandIdentity);

        public Task<IngestionBatchCommandResult> CompleteCommitAsync(
            ImportBatch batch,
            long expectedStoredAggregateRevision,
            IReadOnlyList<IngestionCatalogDeliveryOutcome> outcomes,
            IngestionCommandIdentity commandIdentity,
            string callerIdentity,
            CancellationToken cancellationToken) =>
            Save(batch, commandIdentity);

        public void AdvanceCurrentProjectionForProof()
        {
            var aggregate = Restore(Current);
            aggregate.BeginCommit(Current.AggregateRevision, Current.LastChangedAtUtc);
            Current = IngestionBatchSnapshot.From(aggregate);
        }

        private Task<IngestionBatchCommandResult> Save(
            ImportBatch batch,
            IngestionCommandIdentity commandIdentity)
        {
            Current = IngestionBatchSnapshot.From(batch);
            _results.Add(
                (commandIdentity.Scope, commandIdentity.Key),
                (commandIdentity.RequestDigest, Current));
            return Task.FromResult(new IngestionBatchCommandResult(Current, false));
        }

        private static ImportBatch Restore(IngestionBatchSnapshot snapshot) =>
            ImportBatch.Restore(
                snapshot.Id,
                snapshot.ProducerIdentity,
                snapshot.ProducerBuild,
                snapshot.CollectorExportId,
                snapshot.CollectorExportDigest,
                snapshot.TargetSiteKey,
                snapshot.TargetCatalogKey,
                snapshot.TargetCatalogConfigurationRevisionId,
                snapshot.ExpectedItemCount,
                snapshot.ManifestDigest,
                snapshot.ItemIndexDigest,
                snapshot.PayloadDigest,
                snapshot.PayloadObjectKey,
                snapshot.PayloadObjectDigest,
                snapshot.PayloadObjectSize,
                snapshot.PayloadContentType,
                snapshot.RegisteredAtUtc,
                snapshot.LastChangedAtUtc,
                snapshot.State,
                snapshot.AggregateRevision,
                snapshot.AcceptedItemCount,
                snapshot.ReviewRequiredItemCount,
                snapshot.RejectedItemCount,
                snapshot.FailureCode);
    }
}
