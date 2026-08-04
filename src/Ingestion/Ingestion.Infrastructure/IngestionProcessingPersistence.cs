using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Platform.ObjectStorage;

namespace Aggregator.Ingestion.Infrastructure;

public sealed class IngestionProcessingDbContext(
    DbContextOptions<IngestionProcessingDbContext> options) : DbContext(options)
{
    internal DbSet<ProcessingImportBatchRow> Batches => Set<ProcessingImportBatchRow>();
    internal DbSet<ProcessingValidationJobRow> ValidationJobs => Set<ProcessingValidationJobRow>();
    internal DbSet<ProcessingDecisionRow> Decisions => Set<ProcessingDecisionRow>();
    internal DbSet<ProcessingDeliveryRow> Deliveries => Set<ProcessingDeliveryRow>();
    internal DbSet<ProcessingCommandRow> Commands => Set<ProcessingCommandRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureBatch(modelBuilder);
        ConfigureValidationJob(modelBuilder);
        ConfigureDecision(modelBuilder);
        ConfigureDelivery(modelBuilder);
        ConfigureCommand(modelBuilder);
    }

    private static void ConfigureBatch(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProcessingImportBatchRow>();
        entity.ToTable("import_batch", "batches");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).HasColumnName("id");
        entity.Property(row => row.ProducerIdentity).HasColumnName("producer_identity").HasMaxLength(200);
        entity.Property(row => row.ProducerBuild).HasColumnName("producer_build").HasMaxLength(200);
        entity.Property(row => row.CollectorExportId).HasColumnName("collector_export_id");
        entity.Property(row => row.CollectorExportDigest).HasColumnName("collector_export_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.TargetSiteKey).HasColumnName("target_site_key").HasMaxLength(96);
        entity.Property(row => row.TargetCatalogKey).HasColumnName("target_catalog_key").HasMaxLength(96);
        entity.Property(row => row.TargetCatalogConfigurationRevisionId).HasColumnName("target_catalog_configuration_revision_id");
        entity.Property(row => row.ExpectedItemCount).HasColumnName("expected_item_count");
        entity.Property(row => row.ManifestDigest).HasColumnName("manifest_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.ItemIndexDigest).HasColumnName("item_index_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.PayloadDigest).HasColumnName("payload_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.PayloadObjectKey).HasColumnName("payload_object_key").HasMaxLength(1024);
        entity.Property(row => row.PayloadObjectDigest).HasColumnName("payload_object_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.PayloadObjectSize).HasColumnName("payload_object_size");
        entity.Property(row => row.PayloadContentType).HasColumnName("payload_content_type").HasMaxLength(200);
        entity.Property(row => row.RegisteredAtUtc).HasColumnName("registered_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.LastChangedAtUtc).HasColumnName("last_changed_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.State).HasColumnName("state");
        entity.Property(row => row.AggregateRevision).HasColumnName("aggregate_revision").IsConcurrencyToken();
        entity.Property(row => row.AcceptedItemCount).HasColumnName("accepted_item_count");
        entity.Property(row => row.ReviewRequiredItemCount).HasColumnName("review_required_item_count");
        entity.Property(row => row.RejectedItemCount).HasColumnName("rejected_item_count");
        entity.Property(row => row.FailureCode).HasColumnName("failure_code").HasMaxLength(200);
    }

    private static void ConfigureValidationJob(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProcessingValidationJobRow>();
        entity.ToTable("validation_job", "processing");
        entity.HasKey(row => row.BatchId);
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.State).HasColumnName("state");
        entity.Property(row => row.WorkerIdentity).HasColumnName("worker_identity").HasMaxLength(200);
        entity.Property(row => row.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.AttemptCount).HasColumnName("attempt_count");
        entity.Property(row => row.PayloadDigest).HasColumnName("payload_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.FailureCode).HasColumnName("failure_code").HasMaxLength(200);
        entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.LastChangedAtUtc).HasColumnName("last_changed_at_utc").HasColumnType("timestamp with time zone");
        entity.HasOne<ProcessingImportBatchRow>()
            .WithOne()
            .HasForeignKey<ProcessingValidationJobRow>(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(row => new { row.State, row.LeaseExpiresAtUtc });
    }

    private static void ConfigureDecision(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProcessingDecisionRow>();
        entity.ToTable("item_decision", "processing");
        entity.HasKey(row => row.DecisionId);
        entity.Property(row => row.DecisionId).HasColumnName("decision_id");
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.ItemKey).HasColumnName("item_key").HasMaxLength(200);
        entity.Property(row => row.ItemDigest).HasColumnName("item_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.Decision).HasColumnName("decision");
        entity.Property(row => row.ReasonCodes).HasColumnName("reason_codes").HasColumnType("text[]");
        entity.Property(row => row.SupersedesDecisionId).HasColumnName("supersedes_decision_id");
        entity.Property(row => row.DecidedAtUtc).HasColumnName("decided_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.DecidedBy).HasColumnName("decided_by").HasMaxLength(200);
        entity.Property(row => row.ItemDocument).HasColumnName("item_document").HasColumnType("bytea");
        entity.Property(row => row.ItemDocumentDigest).HasColumnName("item_document_digest").HasMaxLength(64).IsFixedLength();
        entity.HasOne<ProcessingImportBatchRow>()
            .WithMany()
            .HasForeignKey(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne<ProcessingDecisionRow>()
            .WithOne()
            .HasForeignKey<ProcessingDecisionRow>(row => row.SupersedesDecisionId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(row => new { row.BatchId, row.ItemKey, row.DecidedAtUtc, row.DecisionId });
        entity.HasIndex(row => row.SupersedesDecisionId).IsUnique();
    }

    private static void ConfigureDelivery(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProcessingDeliveryRow>();
        entity.ToTable("catalog_delivery", "processing");
        entity.HasKey(row => row.DeliveryId);
        entity.Property(row => row.DeliveryId).HasColumnName("delivery_id");
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.ItemKey).HasColumnName("item_key").HasMaxLength(200);
        entity.Property(row => row.CommandType).HasColumnName("command_type").HasMaxLength(200);
        entity.Property(row => row.CommandDocument).HasColumnName("command_document").HasColumnType("bytea");
        entity.Property(row => row.CommandDigest).HasColumnName("command_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.State).HasColumnName("state");
        entity.Property(row => row.AttemptCount).HasColumnName("attempt_count");
        entity.Property(row => row.WorkerIdentity).HasColumnName("worker_identity").HasMaxLength(200);
        entity.Property(row => row.LeaseExpiresAtUtc).HasColumnName("lease_expires_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.CatalogListingId).HasColumnName("catalog_listing_id");
        entity.Property(row => row.CatalogListingRevisionId).HasColumnName("catalog_listing_revision_id");
        entity.Property(row => row.FailureCode).HasColumnName("failure_code").HasMaxLength(200);
        entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.LastChangedAtUtc).HasColumnName("last_changed_at_utc").HasColumnType("timestamp with time zone");
        entity.HasOne<ProcessingImportBatchRow>()
            .WithMany()
            .HasForeignKey(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(row => new { row.BatchId, row.ItemKey }).IsUnique();
        entity.HasIndex(row => new { row.State, row.LeaseExpiresAtUtc, row.CreatedAtUtc });
    }

    private static void ConfigureCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ProcessingCommandRow>();
        entity.ToTable("command_result", "processing_operations");
        entity.HasKey(row => new { row.Scope, row.Key });
        entity.Property(row => row.Scope).HasColumnName("scope").HasMaxLength(150);
        entity.Property(row => row.Key).HasColumnName("key").HasMaxLength(200);
        entity.Property(row => row.RequestDigest).HasColumnName("request_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.ResultDocument).HasColumnName("result_document").HasColumnType("bytea");
        entity.Property(row => row.ResultDigest).HasColumnName("result_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.CallerIdentity).HasColumnName("caller_identity").HasMaxLength(200);
        entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.HasOne<ProcessingImportBatchRow>()
            .WithMany()
            .HasForeignKey(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EfIngestionProcessingStore(IngestionProcessingDbContext dbContext)
    : IIngestionProcessingStore
{
    public async Task<LeasedIngestionProcessingBatch?> LeaseNextUploadedAsync(
        string workerIdentity,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var batch = await dbContext.Batches
            .FromSqlInterpolated($$"""
                SELECT b.*
                FROM batches.import_batch b
                LEFT JOIN processing.validation_job j ON j.batch_id = b.id
                WHERE b.state = {{(int)ImportBatchState.Uploaded}}
                  AND (j.batch_id IS NULL OR j.state = 3 OR j.lease_expires_at_utc <= {{leasedAtUtc}})
                ORDER BY b.registered_at_utc, b.id
                FOR UPDATE OF b SKIP LOCKED
                LIMIT 1
                """)
            .SingleOrDefaultAsync(cancellationToken);
        if (batch is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var job = await dbContext.ValidationJobs
            .SingleOrDefaultAsync(row => row.BatchId == batch.Id, cancellationToken);
        if (job is null)
        {
            job = new ProcessingValidationJobRow
            {
                BatchId = batch.Id,
                State = 2,
                WorkerIdentity = workerIdentity,
                LeaseExpiresAtUtc = leaseExpiresAtUtc,
                AttemptCount = 1,
                CreatedAtUtc = leasedAtUtc,
                LastChangedAtUtc = leasedAtUtc,
            };
            dbContext.ValidationJobs.Add(job);
        }
        else
        {
            job.State = 2;
            job.WorkerIdentity = workerIdentity;
            job.LeaseExpiresAtUtc = leaseExpiresAtUtc;
            job.AttemptCount++;
            job.FailureCode = null;
            job.LastChangedAtUtc = leasedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LeasedIngestionProcessingBatch(
            ToSnapshot(batch),
            workerIdentity,
            leaseExpiresAtUtc);
    }

    public async Task<IngestionProcessingSnapshot> CompleteValidationAsync(
        Guid batchId,
        long expectedAggregateRevision,
        string payloadDigest,
        IReadOnlyList<IngestionProcessingDecision> decisions,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateExactDecisionCoverage(decisions);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var batch = await RequireBatchAsync(batchId, cancellationToken);
        EnsureBatch(batch, ImportBatchState.Uploaded, expectedAggregateRevision);
        if (!string.Equals(batch.PayloadObjectDigest, payloadDigest, StringComparison.Ordinal))
        {
            throw ProcessingFailure(
                "INGESTION_PAYLOAD_DIGEST_MISMATCH",
                422,
                "The processed payload digest differs from the registered object digest.",
                "Discard the processing result and restore the exact registered object.");
        }

        if (decisions.Count != batch.ExpectedItemCount)
        {
            throw ProcessingFailure(
                "INGESTION_DECISION_COVERAGE_INVALID",
                500,
                "Validation decisions do not cover the exact registered package.",
                "Correct the deterministic validation owner before persistence.");
        }

        foreach (var decision in decisions)
        {
            dbContext.Decisions.Add(ToRow(batch.Id, decision));
        }

        await AdvanceAsync(batch, ImportBatchState.IntegrityChecking, completedAtUtc, cancellationToken);
        await AdvanceAsync(batch, ImportBatchState.IntegrityValid, completedAtUtc, cancellationToken);
        await AdvanceAsync(batch, ImportBatchState.ItemValidation, completedAtUtc, cancellationToken);
        batch.AcceptedItemCount = decisions.Count(item => item.Decision == IngestionProcessingDecisionContract.Accepted);
        batch.ReviewRequiredItemCount = decisions.Count(item => item.Decision == IngestionProcessingDecisionContract.NeedsReview);
        batch.RejectedItemCount = decisions.Count(item => item.Decision == IngestionProcessingDecisionContract.Rejected);
        await AdvanceAsync(
            batch,
            batch.ReviewRequiredItemCount > 0
                ? ImportBatchState.ReviewRequired
                : ImportBatchState.ReadyToCommit,
            completedAtUtc,
            cancellationToken);
        var job = await dbContext.ValidationJobs.SingleAsync(row => row.BatchId == batch.Id, cancellationToken);
        job.State = 3;
        job.PayloadDigest = payloadDigest;
        job.LeaseExpiresAtUtc = null;
        job.LastChangedAtUtc = completedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IngestionProcessingSnapshot(ToSnapshot(batch), decisions);
    }

    public async Task FailValidationAsync(
        Guid batchId,
        long expectedAggregateRevision,
        string failureCode,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var batch = await RequireBatchAsync(batchId, cancellationToken);
        if (batch.State != (int)ImportBatchState.Uploaded ||
            batch.AggregateRevision != expectedAggregateRevision)
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        await AdvanceAsync(batch, ImportBatchState.IntegrityChecking, failedAtUtc, cancellationToken);
        batch.FailureCode = NormalizeFailureCode(failureCode);
        await AdvanceAsync(batch, ImportBatchState.IntegrityFailed, failedAtUtc, cancellationToken);
        var job = await dbContext.ValidationJobs.SingleAsync(row => row.BatchId == batch.Id, cancellationToken);
        job.State = 4;
        job.FailureCode = batch.FailureCode;
        job.LeaseExpiresAtUtc = null;
        job.LastChangedAtUtc = failedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IngestionProcessingSnapshot?> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var batch = await dbContext.Batches
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return null;
        }

        var decisions = await ReadLatestDecisionsAsync(batchId, cancellationToken);
        return new IngestionProcessingSnapshot(ToSnapshot(batch), decisions);
    }

    public async Task<IngestionProcessingSnapshot> CompleteReviewAsync(
        Guid batchId,
        long expectedAggregateRevision,
        IReadOnlyList<ReviewIngestionItemRequest> reviewDecisions,
        string reviewerIdentity,
        DateTimeOffset reviewedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var batch = await RequireBatchAsync(batchId, cancellationToken);
        EnsureBatch(batch, ImportBatchState.ReviewRequired, expectedAggregateRevision);
        var current = await ReadLatestDecisionRowsAsync(batchId, tracking: false, cancellationToken);
        var currentByKey = current.ToDictionary(row => row.ItemKey, StringComparer.Ordinal);
        foreach (var review in reviewDecisions)
        {
            if (!currentByKey.TryGetValue(review.ItemKey, out var existing) ||
                existing.DecisionId != review.ExpectedDecisionId ||
                existing.Decision != (int)IngestionProcessingDecisionContract.NeedsReview)
            {
                throw ProcessingFailure(
                    "INGESTION_REVIEW_DECISION_CONFLICT",
                    409,
                    $"Item '{review.ItemKey}' no longer has the expected review decision.",
                    "Reload the current item decisions and retry.");
            }

            if (review.Decision == IngestionProcessingDecisionContract.NeedsReview)
            {
                throw ProcessingFailure(
                    "INGESTION_REVIEW_DECISION_INVALID",
                    400,
                    "A completed review must accept or reject the item.",
                    "Submit an explicit accepted or rejected review outcome.");
            }

            var item = ProcessingDocument.Deserialize<IngestionCandidatePayloadItem>(existing.ItemDocument);
            var replacement = new IngestionProcessingDecision(
                Guid.CreateVersion7(),
                existing.ItemKey,
                existing.ItemDigest,
                review.Decision,
                [NormalizeReason(review.ReasonCode)],
                existing.DecisionId,
                reviewedAtUtc,
                reviewerIdentity,
                item);
            dbContext.Decisions.Add(ToRow(batch.Id, replacement));
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        var latest = await ReadLatestDecisionRowsAsync(batchId, tracking: false, cancellationToken);
        if (latest.Any(row => row.Decision == (int)IngestionProcessingDecisionContract.NeedsReview))
        {
            throw ProcessingFailure(
                "INGESTION_REVIEW_INCOMPLETE",
                409,
                "Review completion requires an explicit outcome for every item awaiting review.",
                "Submit the remaining current review decisions in the same command.");
        }

        batch.AcceptedItemCount = latest.Count(row => row.Decision == (int)IngestionProcessingDecisionContract.Accepted);
        batch.ReviewRequiredItemCount = 0;
        batch.RejectedItemCount = latest.Count(row => row.Decision == (int)IngestionProcessingDecisionContract.Rejected);
        await AdvanceAsync(batch, ImportBatchState.ReadyToCommit, reviewedAtUtc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new IngestionProcessingSnapshot(
            ToSnapshot(batch),
            latest.Select(ToDecision).ToArray());
    }

    public async Task<IngestionCommitResult> BeginCommitAsync(
        Guid batchId,
        long expectedAggregateRevision,
        IngestionCommandIdentity commandIdentity,
        string callerIdentity,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var replay = await ReadCommitReplayAsync(commandIdentity, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay with { Replayed = true };
        }

        var batch = await RequireBatchAsync(batchId, cancellationToken);
        EnsureBatch(batch, ImportBatchState.ReadyToCommit, expectedAggregateRevision);
        var decisions = await ReadLatestDecisionRowsAsync(batchId, tracking: false, cancellationToken);
        var accepted = decisions
            .Where(row => row.Decision == (int)IngestionProcessingDecisionContract.Accepted)
            .OrderBy(row => row.ItemKey, StringComparer.Ordinal)
            .ToArray();
        var deliveries = new List<IngestionCatalogDeliveryDto>(accepted.Length);
        foreach (var decision in accepted)
        {
            var item = ProcessingDocument.Deserialize<IngestionCandidatePayloadItem>(decision.ItemDocument);
            var deliveryId = Guid.CreateVersion7();
            var command = CreateCatalogCommand(batch, item, deliveryId, requestedAtUtc);
            var document = ProcessingDocument.Serialize(command);
            var digest = ProcessingDocument.ComputeDigest(document);
            dbContext.Deliveries.Add(new ProcessingDeliveryRow
            {
                DeliveryId = deliveryId,
                BatchId = batch.Id,
                ItemKey = item.ItemKey,
                CommandType = CatalogIngestionCommandContracts.UpsertDraft,
                CommandDocument = document,
                CommandDigest = digest,
                State = 1,
                AttemptCount = 0,
                CreatedAtUtc = requestedAtUtc,
                LastChangedAtUtc = requestedAtUtc,
            });
            deliveries.Add(ToDeliveryDto(
                deliveryId,
                batch.Id,
                item.ItemKey,
                CatalogIngestionCommandContracts.UpsertDraft,
                digest,
                state: 1,
                attemptCount: 0,
                null,
                null,
                null,
                requestedAtUtc,
                requestedAtUtc));
        }

        await AdvanceAsync(batch, ImportBatchState.Committing, requestedAtUtc, cancellationToken);
        if (deliveries.Count == 0)
        {
            await AdvanceAsync(batch, ImportBatchState.PartiallyRejected, requestedAtUtc, cancellationToken);
        }

        var processing = new IngestionProcessingSnapshot(
            ToSnapshot(batch),
            decisions.Select(ToDecision).ToArray());
        var result = new IngestionCommitResult(processing, deliveries, Replayed: false);
        var resultDocument = ProcessingDocument.Serialize(result);
        dbContext.Commands.Add(new ProcessingCommandRow
        {
            Scope = commandIdentity.Scope,
            Key = commandIdentity.Key,
            RequestDigest = commandIdentity.RequestDigest,
            BatchId = batch.Id,
            ResultDocument = resultDocument,
            ResultDigest = ProcessingDocument.ComputeDigest(resultDocument),
            CallerIdentity = callerIdentity,
            CreatedAtUtc = requestedAtUtc,
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<PendingIngestionCatalogDelivery>> LeaseCatalogDeliveriesAsync(
        string workerIdentity,
        int limit,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var rows = await dbContext.Deliveries
            .FromSqlInterpolated($$"""
                SELECT *
                FROM processing.catalog_delivery
                WHERE state IN (1, 2)
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= {{leasedAtUtc}})
                ORDER BY created_at_utc, delivery_id
                FOR UPDATE SKIP LOCKED
                LIMIT {{limit}}
                """)
            .ToArrayAsync(cancellationToken);
        foreach (var row in rows)
        {
            row.State = 2;
            row.AttemptCount++;
            row.WorkerIdentity = workerIdentity;
            row.LeaseExpiresAtUtc = leaseExpiresAtUtc;
            row.LastChangedAtUtc = leasedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return rows.Select(row => new PendingIngestionCatalogDelivery(
            row.DeliveryId,
            row.BatchId,
            row.ItemKey,
            ProcessingDocument.Deserialize<CatalogIngestionUpsertDraftCommand>(row.CommandDocument),
            row.CommandDigest,
            row.AttemptCount)).ToArray();
    }

    public async Task<IngestionProcessingSnapshot> RecordCatalogOutcomeAsync(
        IngestionCatalogDeliveryOutcome outcome,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var delivery = await dbContext.Deliveries
            .SingleOrDefaultAsync(row => row.DeliveryId == outcome.DeliveryId, cancellationToken)
            ?? throw ProcessingFailure(
                "INGESTION_DELIVERY_NOT_FOUND",
                404,
                $"Catalog delivery '{outcome.DeliveryId:D}' was not found.",
                "Use the exact delivery identity emitted by Ingestion.");
        if (delivery.BatchId != outcome.BatchId ||
            !string.Equals(delivery.ItemKey, outcome.ItemKey, StringComparison.Ordinal) ||
            outcome.Outcome.CommandId != delivery.DeliveryId)
        {
            throw ProcessingFailure(
                "INGESTION_DELIVERY_OUTCOME_IDENTITY_MISMATCH",
                409,
                "The Catalog outcome identifies a different Ingestion delivery.",
                "Replay the exact producer-owned outcome for this delivery identity.");
        }

        var terminalState = outcome.Outcome.State == CatalogIngestionOutcomeStateContract.Rejected ? 4 : 3;
        if (delivery.State is 3 or 4)
        {
            if (delivery.State != terminalState ||
                delivery.CatalogListingId != outcome.Outcome.ListingId ||
                delivery.CatalogListingRevisionId != outcome.Outcome.ListingRevisionId ||
                !string.Equals(delivery.FailureCode, outcome.Outcome.FailureCode, StringComparison.Ordinal))
            {
                throw ProcessingFailure(
                    "INGESTION_DELIVERY_OUTCOME_CONFLICT",
                    409,
                    "The delivery already has a different terminal Catalog outcome.",
                    "Use the exact original Catalog outcome.");
            }
        }
        else
        {
            delivery.State = terminalState;
            delivery.CatalogListingId = outcome.Outcome.ListingId;
            delivery.CatalogListingRevisionId = outcome.Outcome.ListingRevisionId;
            delivery.FailureCode = outcome.Outcome.FailureCode;
            delivery.WorkerIdentity = null;
            delivery.LeaseExpiresAtUtc = null;
            delivery.LastChangedAtUtc = completedAtUtc;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var batch = await RequireBatchAsync(delivery.BatchId, cancellationToken);
        var allDeliveries = await dbContext.Deliveries
            .Where(row => row.BatchId == batch.Id)
            .ToArrayAsync(cancellationToken);
        if (batch.State == (int)ImportBatchState.Committing &&
            allDeliveries.All(row => row.State is 3 or 4))
        {
            var delivered = allDeliveries.Count(row => row.State == 3);
            var deliveryRejected = allDeliveries.Count(row => row.State == 4);
            batch.AcceptedItemCount = delivered;
            batch.RejectedItemCount += deliveryRejected;
            await AdvanceAsync(
                batch,
                batch.RejectedItemCount == 0
                    ? ImportBatchState.Committed
                    : ImportBatchState.PartiallyRejected,
                completedAtUtc,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var decisions = await ReadLatestDecisionsAsync(batch.Id, cancellationToken);
        return new IngestionProcessingSnapshot(ToSnapshot(batch), decisions);
    }

    private async Task<IngestionCommitResult?> ReadCommitReplayAsync(
        IngestionCommandIdentity identity,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.Commands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                command => command.Scope == identity.Scope && command.Key == identity.Key,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (!string.Equals(row.RequestDigest, identity.RequestDigest, StringComparison.Ordinal))
        {
            throw ProcessingFailure(
                "INGESTION_IDEMPOTENCY_DIGEST_CONFLICT",
                409,
                "The Idempotency-Key was already used for another commit request.",
                "Replay the exact original request or use a new stable key.");
        }

        var actualDigest = ProcessingDocument.ComputeDigest(row.ResultDocument);
        if (!string.Equals(actualDigest, row.ResultDigest, StringComparison.Ordinal))
        {
            throw ProcessingFailure(
                "INGESTION_COMMIT_RESULT_DIGEST_MISMATCH",
                500,
                "A persisted commit result failed digest verification.",
                "Restore the result from a verified Ingestion database backup.");
        }

        return ProcessingDocument.Deserialize<IngestionCommitResult>(row.ResultDocument);
    }

    private async Task<ProcessingImportBatchRow> RequireBatchAsync(
        Guid batchId,
        CancellationToken cancellationToken) =>
        await dbContext.Batches.SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken)
        ?? throw ProcessingFailure(
            "INGESTION_BATCH_NOT_FOUND",
            404,
            $"Import batch '{batchId:D}' was not found.",
            "Use the exact ImportBatchId returned by registration.");

    private static void EnsureBatch(
        ProcessingImportBatchRow batch,
        ImportBatchState expectedState,
        long expectedAggregateRevision)
    {
        if (batch.State != (int)expectedState)
        {
            throw ProcessingFailure(
                "INGESTION_BATCH_STATE_INVALID",
                409,
                $"Import batch state '{(ImportBatchState)batch.State}' cannot execute a command requiring '{expectedState}'.",
                "Reload the current batch state before retrying.");
        }

        if (batch.AggregateRevision != expectedAggregateRevision)
        {
            throw ProcessingFailure(
                "INGESTION_BATCH_REVISION_CONFLICT",
                409,
                $"Expected batch revision {expectedAggregateRevision}, actual revision {batch.AggregateRevision}.",
                "Reload the current batch and retry with its exact aggregate revision.");
        }
    }

    private async Task AdvanceAsync(
        ProcessingImportBatchRow batch,
        ImportBatchState state,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        batch.State = (int)state;
        batch.AggregateRevision++;
        batch.LastChangedAtUtc = changedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ProcessingDecisionRow>> ReadLatestDecisionRowsAsync(
        Guid batchId,
        bool tracking,
        CancellationToken cancellationToken)
    {
        IQueryable<ProcessingDecisionRow> query = dbContext.Decisions.Where(row => row.BatchId == batchId);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        var rows = await query
            .OrderBy(row => row.ItemKey)
            .ThenByDescending(row => row.DecidedAtUtc)
            .ThenByDescending(row => row.DecisionId)
            .ToArrayAsync(cancellationToken);
        return rows
            .GroupBy(row => row.ItemKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(row => row.ItemKey, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<IReadOnlyList<IngestionProcessingDecision>> ReadLatestDecisionsAsync(
        Guid batchId,
        CancellationToken cancellationToken) =>
        (await ReadLatestDecisionRowsAsync(batchId, tracking: false, cancellationToken))
        .Select(ToDecision)
        .ToArray();

    private static void ValidateExactDecisionCoverage(IReadOnlyList<IngestionProcessingDecision> decisions)
    {
        if (decisions.Count == 0 ||
            decisions.Select(decision => decision.ItemKey).Distinct(StringComparer.Ordinal).Count() != decisions.Count ||
            decisions.Select(decision => decision.DecisionId).Distinct().Count() != decisions.Count)
        {
            throw ProcessingFailure(
                "INGESTION_DECISION_IDENTITY_INVALID",
                500,
                "Validation decisions must have unique item and decision identities.",
                "Correct deterministic validation before persistence.");
        }
    }

    private static ProcessingDecisionRow ToRow(
        Guid batchId,
        IngestionProcessingDecision decision)
    {
        var document = ProcessingDocument.Serialize(decision.Item);
        var digest = ProcessingDocument.ComputeDigest(document);
        if (!string.Equals(digest, decision.ItemDigest, StringComparison.Ordinal))
        {
            throw ProcessingFailure(
                "INGESTION_ITEM_DOCUMENT_DIGEST_MISMATCH",
                500,
                "The decision item document differs from its validated digest.",
                "Correct validation result construction before persistence.");
        }

        return new ProcessingDecisionRow
        {
            DecisionId = decision.DecisionId,
            BatchId = batchId,
            ItemKey = decision.ItemKey,
            ItemDigest = decision.ItemDigest,
            Decision = (int)decision.Decision,
            ReasonCodes = decision.ReasonCodes.ToArray(),
            SupersedesDecisionId = decision.SupersedesDecisionId,
            DecidedAtUtc = decision.DecidedAtUtc,
            DecidedBy = decision.DecidedBy,
            ItemDocument = document,
            ItemDocumentDigest = digest,
        };
    }

    private static IngestionProcessingDecision ToDecision(ProcessingDecisionRow row)
    {
        var actualDigest = ProcessingDocument.ComputeDigest(row.ItemDocument);
        if (!string.Equals(actualDigest, row.ItemDocumentDigest, StringComparison.Ordinal) ||
            !string.Equals(actualDigest, row.ItemDigest, StringComparison.Ordinal))
        {
            throw ProcessingFailure(
                "INGESTION_ITEM_DOCUMENT_CORRUPT",
                500,
                "A persisted item decision failed exact document digest verification.",
                "Restore the decision from a verified Ingestion database backup.");
        }

        return new IngestionProcessingDecision(
            row.DecisionId,
            row.ItemKey,
            row.ItemDigest,
            (IngestionProcessingDecisionContract)row.Decision,
            row.ReasonCodes,
            row.SupersedesDecisionId,
            row.DecidedAtUtc,
            row.DecidedBy,
            ProcessingDocument.Deserialize<IngestionCandidatePayloadItem>(row.ItemDocument));
    }

    private static CatalogIngestionUpsertDraftCommand CreateCatalogCommand(
        ProcessingImportBatchRow batch,
        IngestionCandidatePayloadItem item,
        Guid commandId,
        DateTimeOffset requestedAtUtc)
    {
        var fields = item.Fields
            .OrderBy(field => field.FieldKey, StringComparer.Ordinal)
            .ThenBy(field => field.Locale, StringComparer.OrdinalIgnoreCase)
            .Select(field => new CatalogDraftFieldValueContract(
                field.FieldKey,
                MapValueKind(field.Kind),
                field.CanonicalValue,
                field.Locale,
                field.SourceKey,
                field.EvidenceDigest,
                field.UsagePolicy))
            .ToArray();
        var digestInput = new CatalogIngestionCommandDigestInput(
            commandId,
            batch.Id,
            item.ItemKey,
            batch.TargetSiteKey,
            batch.TargetCatalogKey,
            batch.TargetCatalogConfigurationRevisionId,
            item.EntityKind,
            item.SubjectNaturalKey,
            fields,
            requestedAtUtc);
        var commandDigest = CatalogIngestionCommandDigest.Compute(digestInput);
        return new CatalogIngestionUpsertDraftCommand(
            commandId,
            batch.Id,
            item.ItemKey,
            commandDigest,
            batch.TargetSiteKey,
            batch.TargetCatalogKey,
            batch.TargetCatalogConfigurationRevisionId,
            item.EntityKind,
            item.SubjectNaturalKey,
            fields,
            requestedAtUtc,
            $"ingestion:{batch.Id:N}:{commandId:N}");
    }

    private static CatalogDraftValueKindContract MapValueKind(
        IngestionCandidateFieldValueKindContract value) => value switch
        {
            IngestionCandidateFieldValueKindContract.Text => CatalogDraftValueKindContract.Text,
            IngestionCandidateFieldValueKindContract.Integer => CatalogDraftValueKindContract.Integer,
            IngestionCandidateFieldValueKindContract.Decimal => CatalogDraftValueKindContract.Decimal,
            IngestionCandidateFieldValueKindContract.Boolean => CatalogDraftValueKindContract.Boolean,
            IngestionCandidateFieldValueKindContract.Date => CatalogDraftValueKindContract.Date,
            IngestionCandidateFieldValueKindContract.DateTime => CatalogDraftValueKindContract.DateTime,
            IngestionCandidateFieldValueKindContract.Uri => CatalogDraftValueKindContract.Uri,
            IngestionCandidateFieldValueKindContract.ExternalReference => CatalogDraftValueKindContract.ExternalReference,
            _ => throw ProcessingFailure(
                "INGESTION_FIELD_KIND_UNSUPPORTED",
                500,
                "A validated candidate field kind cannot be mapped to Catalog.",
                "Correct the producer contract mapping."),
        };

    private static IngestionCatalogDeliveryDto ToDeliveryDto(
        Guid deliveryId,
        Guid batchId,
        string itemKey,
        string commandType,
        string commandDigest,
        int state,
        int attemptCount,
        Guid? listingId,
        Guid? revisionId,
        string? failureCode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset changedAtUtc) =>
        new(
            deliveryId,
            itemKey,
            commandType,
            commandDigest,
            DeliveryStateName(state),
            attemptCount,
            listingId,
            revisionId,
            failureCode,
            createdAtUtc,
            changedAtUtc);

    private static string DeliveryStateName(int state) => state switch
    {
        1 => "pending",
        2 => "published",
        3 => "succeeded",
        4 => "rejected",
        _ => throw ProcessingFailure(
            "INGESTION_DELIVERY_STATE_CORRUPT",
            500,
            "A persisted Catalog delivery state is unsupported.",
            "Repair the delivery through an owner migration or restore operation."),
    };

    private static ProcessingImportBatchRow ToRow(IngestionBatchSnapshot batch) =>
        new()
        {
            Id = batch.Id.Value,
            ProducerIdentity = batch.ProducerIdentity,
            ProducerBuild = batch.ProducerBuild,
            CollectorExportId = batch.CollectorExportId,
            CollectorExportDigest = batch.CollectorExportDigest,
            TargetSiteKey = batch.TargetSiteKey,
            TargetCatalogKey = batch.TargetCatalogKey,
            TargetCatalogConfigurationRevisionId = batch.TargetCatalogConfigurationRevisionId,
            ExpectedItemCount = batch.ExpectedItemCount,
            ManifestDigest = batch.ManifestDigest,
            ItemIndexDigest = batch.ItemIndexDigest,
            PayloadDigest = batch.PayloadDigest,
            PayloadObjectKey = batch.PayloadObjectKey,
            PayloadObjectDigest = batch.PayloadObjectDigest,
            PayloadObjectSize = batch.PayloadObjectSize,
            PayloadContentType = batch.PayloadContentType,
            RegisteredAtUtc = batch.RegisteredAtUtc,
            LastChangedAtUtc = batch.LastChangedAtUtc,
            State = (int)batch.State,
            AggregateRevision = batch.AggregateRevision,
            AcceptedItemCount = batch.AcceptedItemCount,
            ReviewRequiredItemCount = batch.ReviewRequiredItemCount,
            RejectedItemCount = batch.RejectedItemCount,
            FailureCode = batch.FailureCode,
        };

    private static IngestionBatchSnapshot ToSnapshot(ProcessingImportBatchRow row) =>
        new(
            ImportBatchId.Create(row.Id),
            row.ProducerIdentity,
            row.ProducerBuild,
            row.CollectorExportId,
            row.CollectorExportDigest,
            row.TargetSiteKey,
            row.TargetCatalogKey,
            row.TargetCatalogConfigurationRevisionId,
            row.ExpectedItemCount,
            row.ManifestDigest,
            row.ItemIndexDigest,
            row.PayloadDigest,
            row.PayloadObjectKey,
            row.PayloadObjectDigest,
            row.PayloadObjectSize,
            row.PayloadContentType,
            row.RegisteredAtUtc,
            row.LastChangedAtUtc,
            (ImportBatchState)row.State,
            row.AggregateRevision,
            row.AcceptedItemCount,
            row.ReviewRequiredItemCount,
            row.RejectedItemCount,
            row.FailureCode);

    private static string NormalizeFailureCode(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            value.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-' or ':' or '.')))
        {
            throw ProcessingFailure(
                "INGESTION_FAILURE_CODE_INVALID",
                500,
                "A generated processing failure code is invalid.",
                "Correct the processing failure classifier.");
        }

        return value;
    }

    private static string NormalizeReason(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
            value.Any(character => !(char.IsLower(character) || char.IsDigit(character) || character is '_' or '-' or ':')))
        {
            throw ProcessingFailure(
                "INGESTION_REASON_CODE_INVALID",
                400,
                "Reason codes must be bounded lowercase semantic keys.",
                "Submit one documented review or failure reason code.");
        }

        return value;
    }

    private static IngestionApplicationException ProcessingFailure(
        string code,
        int statusCode,
        string detail,
        string requiredAction) =>
        new(
            "Ingestion.Processing",
            code,
            statusCode,
            detail,
            requiredAction);

    private sealed record CatalogCommandDigestInput(
        Guid CommandId,
        Guid IngestionBatchId,
        string IngestionItemKey,
        string SiteKey,
        string CatalogKey,
        Guid ExpectedCatalogConfigurationRevisionId,
        string EntityKind,
        string SubjectNaturalKey,
        IReadOnlyList<CatalogDraftFieldValueContract> Fields,
        DateTimeOffset RequestedAtUtc);
}

public sealed class ObjectStoreIngestionProcessingPayloadReader(IObjectStore objectStore)
    : IIngestionProcessingPayloadReader
{
    public async Task<Stream> OpenVerifiedAsync(
        string objectKey,
        string expectedDigest,
        long expectedSize,
        string expectedContentType,
        CancellationToken cancellationToken)
    {
        var descriptor = await objectStore.HeadAsync(objectKey, cancellationToken);
        if (descriptor.Size != expectedSize ||
            !string.Equals(descriptor.Sha256, expectedDigest, StringComparison.Ordinal) ||
            !string.Equals(descriptor.ContentType, expectedContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The Ingestion payload object metadata does not match the registered package identity.");
        }

        return await objectStore.OpenReadVerifiedAsync(
            objectKey,
            expectedDigest,
            cancellationToken);
    }
}

public static class IngestionProcessingInfrastructureExtensions
{
    public static IServiceCollection AddIngestionProcessingInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Ingestion")
            ?? throw new InvalidOperationException("Connection string 'Ingestion' is required.");
        services.AddDbContext<IngestionProcessingDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<IIngestionProcessingStore, EfIngestionProcessingStore>();
        services.AddScoped<IIngestionProcessingPayloadReader, ObjectStoreIngestionProcessingPayloadReader>();
        return services;
    }
}

internal static class ProcessingDocument
{
    public static byte[] Serialize<T>(T value) =>
        IngestionCanonicalJson.Serialize(value);

    public static T Deserialize<T>(ReadOnlySpan<byte> document) =>
        IngestionCanonicalJson.Deserialize<T>(document);

    public static string ComputeDigest(ReadOnlySpan<byte> document) =>
        IngestionCanonicalJson.ComputeDigest(document);
}

internal sealed class ProcessingImportBatchRow
{
    public Guid Id { get; set; }
    public string ProducerIdentity { get; set; } = string.Empty;
    public string ProducerBuild { get; set; } = string.Empty;
    public Guid CollectorExportId { get; set; }
    public string CollectorExportDigest { get; set; } = string.Empty;
    public string TargetSiteKey { get; set; } = string.Empty;
    public string TargetCatalogKey { get; set; } = string.Empty;
    public Guid TargetCatalogConfigurationRevisionId { get; set; }
    public int ExpectedItemCount { get; set; }
    public string ManifestDigest { get; set; } = string.Empty;
    public string ItemIndexDigest { get; set; } = string.Empty;
    public string PayloadDigest { get; set; } = string.Empty;
    public string PayloadObjectKey { get; set; } = string.Empty;
    public string PayloadObjectDigest { get; set; } = string.Empty;
    public long PayloadObjectSize { get; set; }
    public string PayloadContentType { get; set; } = string.Empty;
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public DateTimeOffset LastChangedAtUtc { get; set; }
    public int State { get; set; }
    public long AggregateRevision { get; set; }
    public int AcceptedItemCount { get; set; }
    public int ReviewRequiredItemCount { get; set; }
    public int RejectedItemCount { get; set; }
    public string? FailureCode { get; set; }
}

internal sealed class ProcessingValidationJobRow
{
    public Guid BatchId { get; set; }
    public int State { get; set; }
    public string? WorkerIdentity { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? PayloadDigest { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastChangedAtUtc { get; set; }
}

internal sealed class ProcessingDecisionRow
{
    public Guid DecisionId { get; set; }
    public Guid BatchId { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public string ItemDigest { get; set; } = string.Empty;
    public int Decision { get; set; }
    public string[] ReasonCodes { get; set; } = [];
    public Guid? SupersedesDecisionId { get; set; }
    public DateTimeOffset DecidedAtUtc { get; set; }
    public string DecidedBy { get; set; } = string.Empty;
    public byte[] ItemDocument { get; set; } = [];
    public string ItemDocumentDigest { get; set; } = string.Empty;
}

internal sealed class ProcessingDeliveryRow
{
    public Guid DeliveryId { get; set; }
    public Guid BatchId { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public string CommandType { get; set; } = string.Empty;
    public byte[] CommandDocument { get; set; } = [];
    public string CommandDigest { get; set; } = string.Empty;
    public int State { get; set; }
    public int AttemptCount { get; set; }
    public string? WorkerIdentity { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public Guid? CatalogListingId { get; set; }
    public Guid? CatalogListingRevisionId { get; set; }
    public string? FailureCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastChangedAtUtc { get; set; }
}

internal sealed class ProcessingCommandRow
{
    public string Scope { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestDigest { get; set; } = string.Empty;
    public Guid BatchId { get; set; }
    public byte[] ResultDocument { get; set; } = [];
    public string ResultDigest { get; set; } = string.Empty;
    public string CallerIdentity { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
