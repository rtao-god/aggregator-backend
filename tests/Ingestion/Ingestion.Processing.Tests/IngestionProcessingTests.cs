using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;
using Aggregator.Ingestion.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ingestion.Processing.Tests;

public sealed class IngestionProcessingTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 4, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactPayloadClassifiesEveryItemExplicitly()
    {
        var payload = new IngestionCandidatePayloadDocument(
            IngestionCandidatePayloadContract.Identity,
            IngestionCandidatePayloadContract.Revision,
            Guid.Parse("019b9f00-0000-7000-8000-000000000101"),
            new string('a', 64),
            [
                Item("accepted", "public_allowed", IngestionCandidateFieldValueKindContract.Text),
                Item("review", "internal_review_only", IngestionCandidateFieldValueKindContract.Text),
                Item("rejected", "forbidden", IngestionCandidateFieldValueKindContract.Text),
            ]);
        var bytes = IngestionCanonicalJson.Serialize(payload);
        var digest = IngestionCanonicalJson.ComputeDigest(bytes);
        var store = new TestProcessingStore(CreateUploadedBatch(payload, bytes.Length, digest));
        var service = new ValidateIngestionPackageService(
            store,
            new FixedPayloadReader(bytes),
            new FixedTimeProvider(Now));

        var processed = await service.ProcessNextAsync(
            "validation-test-worker",
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.True(processed);
        Assert.Null(store.FailureCode);
        var result = Assert.IsType<IngestionProcessingSnapshot>(store.ValidationResult);
        Assert.Equal(3, result.Decisions.Count);
        Assert.Equal(
            [
                IngestionProcessingDecisionContract.Accepted,
                IngestionProcessingDecisionContract.Rejected,
                IngestionProcessingDecisionContract.NeedsReview,
            ],
            result.Decisions
                .OrderBy(decision => decision.ItemKey, StringComparer.Ordinal)
                .Select(decision => decision.Decision)
                .ToArray());
        Assert.All(result.Decisions, decision => Assert.False(string.IsNullOrWhiteSpace(decision.ItemDigest)));
    }

    [Fact]
    public async Task DuplicateItemKeyFailsCompletePackageBeforePartialPersistence()
    {
        var duplicated = Item("same", "public_allowed", IngestionCandidateFieldValueKindContract.Text);
        var payload = new IngestionCandidatePayloadDocument(
            IngestionCandidatePayloadContract.Identity,
            IngestionCandidatePayloadContract.Revision,
            Guid.Parse("019b9f00-0000-7000-8000-000000000101"),
            new string('a', 64),
            [duplicated, duplicated]);
        var bytes = IngestionCanonicalJson.Serialize(payload);
        var digest = IngestionCanonicalJson.ComputeDigest(bytes);
        var store = new TestProcessingStore(CreateUploadedBatch(payload, bytes.Length, digest));
        var service = new ValidateIngestionPackageService(
            store,
            new FixedPayloadReader(bytes),
            new FixedTimeProvider(Now));

        var processed = await service.ProcessNextAsync(
            "validation-test-worker",
            TimeSpan.FromMinutes(1),
            CancellationToken.None);

        Assert.True(processed);
        Assert.Equal("INGESTION_ITEM_KEY_DUPLICATE", store.FailureCode);
        Assert.Null(store.ValidationResult);
    }

    [Fact]
    public async Task ReviewCommandRejectsDuplicateItemInstructionsBeforeStoreMutation()
    {
        var store = new TestProcessingStore(CreateUploadedBatch(
            new IngestionCandidatePayloadDocument(
                IngestionCandidatePayloadContract.Identity,
                IngestionCandidatePayloadContract.Revision,
                Guid.Parse("019b9f00-0000-7000-8000-000000000101"),
                new string('a', 64),
                [Item("review", "internal_review_only", IngestionCandidateFieldValueKindContract.Text)]),
            payloadSize: 1,
            payloadDigest: new string('b', 64)));
        var service = new ReviewIngestionPackageService(
            store,
            new FixedTimeProvider(Now));
        var decisionId = Guid.CreateVersion7();
        var request = new CompleteIngestionReviewRequest(
            ExpectedAggregateRevision: 7,
            [
                new ReviewIngestionItemRequest(
                    "review",
                    decisionId,
                    IngestionProcessingDecisionContract.Accepted,
                    "review_accepted"),
                new ReviewIngestionItemRequest(
                    "review",
                    decisionId,
                    IngestionProcessingDecisionContract.Rejected,
                    "review_rejected"),
            ]);

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.CompleteAsync(
                store.Batch.Id.Value,
                request,
                "reviewer-test",
                CancellationToken.None));

        Assert.Equal("INGESTION_REVIEW_DECISIONS_INVALID", exception.Code);
        Assert.Equal(0, store.ReviewCallCount);
    }

    [Fact]
    public void CatalogDraftCommandHasNoPublicationAuthority()
    {
        var propertyNames = typeof(CatalogIngestionUpsertDraftCommand)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        var methodNames = typeof(IIngestionCatalogCommandPublisher)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(propertyNames, name =>
            name.Contains("Publish", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Activate", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["PublishAsync"], methodNames);
        Assert.Equal(
            CatalogIngestionCommandContracts.UpsertDraft,
            "aggregator.catalog.ingestion.upsert-draft@1");
    }

    [Fact]
    public void PersistenceModelOwnsImmutableDecisionsAndDeliveryLedger()
    {
        using var context = CreateContext();
        var model = context.GetService<IDesignTimeModel>().Model;
        var decision = FindTable(model, "processing", "item_decision");
        var delivery = FindTable(model, "processing", "catalog_delivery");
        var command = FindTable(model, "processing_operations", "command_result");

        Assert.Equal(["DecisionId"], decision.FindPrimaryKey()!.Properties.Select(item => item.Name));
        Assert.Contains(
            decision.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["SupersedesDecisionId"]));
        Assert.Contains(
            delivery.GetIndexes(),
            index => index.IsUnique &&
                     index.Properties.Select(property => property.Name)
                         .SequenceEqual(["BatchId", "ItemKey"]));
        Assert.Equal(
            ["Scope", "Key"],
            command.FindPrimaryKey()!.Properties.Select(property => property.Name).ToArray());
    }

    private static IngestionCandidatePayloadItem Item(
        string itemKey,
        string usagePolicy,
        IngestionCandidateFieldValueKindContract kind) =>
        new(
            itemKey,
            "provider",
            $"provider:{itemKey}",
            [
                new IngestionCandidateFieldContract(
                    "name",
                    kind,
                    itemKey,
                    "en",
                    "source-one",
                    new string('c', 64),
                    usagePolicy),
            ]);

    private static IngestionBatchSnapshot CreateUploadedBatch(
        IngestionCandidatePayloadDocument payload,
        long payloadSize,
        string payloadDigest) =>
        new(
            ImportBatchId.Create(Guid.Parse("019b9f00-0000-7000-8000-000000000201")),
            "collector-berlin",
            "build-1",
            payload.CollectorExportId,
            payload.CollectorExportDigest,
            "berlin",
            "berlin",
            Guid.Parse("019b9f00-0000-7000-8000-000000000202"),
            payload.Items.Count,
            new string('d', 64),
            new string('e', 64),
            payloadDigest,
            "ingestion/quarantine/package.json",
            payloadDigest,
            payloadSize,
            "application/json",
            Now - TimeSpan.FromMinutes(5),
            Now - TimeSpan.FromMinutes(1),
            ImportBatchState.Uploaded,
            AggregateRevision: 3,
            AcceptedItemCount: 0,
            ReviewRequiredItemCount: 0,
            RejectedItemCount: 0,
            FailureCode: null);

    private static IngestionProcessingDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IngestionProcessingDbContext>()
            .UseNpgsql("Host=localhost;Database=ingestion_db;Username=ingestion_app;Password=test")
            .Options;
        return new IngestionProcessingDbContext(options);
    }

    private static IEntityType FindTable(IModel model, string schema, string tableName) =>
        model.GetEntityTypes().Single(entity =>
            string.Equals(entity.GetSchema(), schema, StringComparison.Ordinal) &&
            string.Equals(entity.GetTableName(), tableName, StringComparison.Ordinal));

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FixedPayloadReader(byte[] payload) : IIngestionProcessingPayloadReader
    {
        public Task<Stream> OpenVerifiedAsync(
            string objectKey,
            string expectedDigest,
            long expectedSize,
            string expectedContentType,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(payload.LongLength, expectedSize);
            Assert.Equal(IngestionCanonicalJson.ComputeDigest(payload), expectedDigest);
            Assert.Equal("application/json", expectedContentType);
            return Task.FromResult<Stream>(new MemoryStream(payload, writable: false));
        }
    }

    private sealed class TestProcessingStore(IngestionBatchSnapshot batch) : IIngestionProcessingStore
    {
        public IngestionBatchSnapshot Batch { get; } = batch;

        public IngestionProcessingSnapshot? ValidationResult { get; private set; }

        public string? FailureCode { get; private set; }

        public int ReviewCallCount { get; private set; }

        public Task<LeasedIngestionProcessingBatch?> LeaseNextUploadedAsync(
            string workerIdentity,
            DateTimeOffset leasedAtUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<LeasedIngestionProcessingBatch?>(
                new LeasedIngestionProcessingBatch(Batch, workerIdentity, leaseExpiresAtUtc));

        public Task<IngestionProcessingSnapshot> CompleteValidationAsync(
            Guid batchId,
            long expectedAggregateRevision,
            string payloadDigest,
            IReadOnlyList<IngestionProcessingDecision> decisions,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken)
        {
            Assert.Equal(Batch.Id.Value, batchId);
            Assert.Equal(Batch.AggregateRevision, expectedAggregateRevision);
            Assert.Equal(Batch.PayloadObjectDigest, payloadDigest);
            var accepted = decisions.Count(item => item.Decision == IngestionProcessingDecisionContract.Accepted);
            var review = decisions.Count(item => item.Decision == IngestionProcessingDecisionContract.NeedsReview);
            var rejected = decisions.Count(item => item.Decision == IngestionProcessingDecisionContract.Rejected);
            var state = review > 0 ? ImportBatchState.ReviewRequired : ImportBatchState.ReadyToCommit;
            var updated = Batch with
            {
                State = state,
                AggregateRevision = 7,
                AcceptedItemCount = accepted,
                ReviewRequiredItemCount = review,
                RejectedItemCount = rejected,
                LastChangedAtUtc = completedAtUtc,
            };
            ValidationResult = new IngestionProcessingSnapshot(updated, decisions);
            return Task.FromResult(ValidationResult);
        }

        public Task FailValidationAsync(
            Guid batchId,
            long expectedAggregateRevision,
            string failureCode,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            FailureCode = failureCode;
            return Task.CompletedTask;
        }

        public Task<IngestionProcessingSnapshot?> ReadAsync(
            Guid batchId,
            CancellationToken cancellationToken) =>
            Task.FromResult(ValidationResult);

        public Task<IngestionProcessingSnapshot> CompleteReviewAsync(
            Guid batchId,
            long expectedAggregateRevision,
            IReadOnlyList<ReviewIngestionItemRequest> reviewDecisions,
            string reviewerIdentity,
            DateTimeOffset reviewedAtUtc,
            CancellationToken cancellationToken)
        {
            ReviewCallCount++;
            throw new NotSupportedException();
        }

        public Task<IngestionCommitResult> BeginCommitAsync(
            Guid batchId,
            long expectedAggregateRevision,
            IngestionCommandIdentity commandIdentity,
            string callerIdentity,
            DateTimeOffset requestedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PendingIngestionCatalogDelivery>> LeaseCatalogDeliveriesAsync(
            string workerIdentity,
            int limit,
            DateTimeOffset leasedAtUtc,
            DateTimeOffset leaseExpiresAtUtc,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PendingIngestionCatalogDelivery>>([]);

        public Task<IngestionProcessingSnapshot> RecordCatalogOutcomeAsync(
            IngestionCatalogDeliveryOutcome outcome,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
