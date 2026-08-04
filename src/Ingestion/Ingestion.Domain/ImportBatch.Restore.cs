namespace Aggregator.Ingestion.Domain;

public sealed partial class ImportBatch
{
    /// <summary>Restores one persisted aggregate only after revalidating every durable invariant.</summary>
    public static ImportBatch Restore(
        ImportBatchId id,
        string producerIdentity,
        string producerBuild,
        Guid collectorExportId,
        string collectorExportDigest,
        string targetSiteKey,
        string targetCatalogKey,
        Guid targetCatalogConfigurationRevisionId,
        int expectedItemCount,
        string manifestDigest,
        string itemIndexDigest,
        string payloadDigest,
        string payloadObjectKey,
        string payloadObjectDigest,
        long payloadObjectSize,
        string payloadContentType,
        DateTimeOffset registeredAtUtc,
        DateTimeOffset lastChangedAtUtc,
        ImportBatchState state,
        long aggregateRevision,
        int acceptedItemCount,
        int reviewRequiredItemCount,
        int rejectedItemCount,
        string? failureCode)
    {
        var batch = Create(
            id,
            producerIdentity,
            producerBuild,
            collectorExportId,
            collectorExportDigest,
            targetSiteKey,
            targetCatalogKey,
            targetCatalogConfigurationRevisionId,
            expectedItemCount,
            manifestDigest,
            itemIndexDigest,
            payloadDigest,
            payloadObjectKey,
            payloadObjectDigest,
            payloadObjectSize,
            payloadContentType,
            registeredAtUtc);
        IngestionContractRules.RequireUtc(lastChangedAtUtc, nameof(lastChangedAtUtc));
        if (lastChangedAtUtc < registeredAtUtc)
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_TIME_REGRESSION",
                "The persisted batch timestamp precedes its registration timestamp.");
        }

        if (!Enum.IsDefined(state))
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_STATE_INVALID",
                $"Persisted import batch state '{state}' is unsupported.");
        }

        var minimumRevision = MinimumAggregateRevision(state);
        if (aggregateRevision < minimumRevision)
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_REVISION_INVALID",
                $"State '{state}' requires aggregate revision at least {minimumRevision}, actual revision {aggregateRevision}.");
        }

        ValidateRestoredDecisionCounts(
            state,
            expectedItemCount,
            acceptedItemCount,
            reviewRequiredItemCount,
            rejectedItemCount);
        ValidateRestoredFailure(state, failureCode);
        batch.LastChangedAtUtc = lastChangedAtUtc;
        batch.State = state;
        batch.AggregateRevision = aggregateRevision;
        batch.AcceptedItemCount = acceptedItemCount;
        batch.ReviewRequiredItemCount = reviewRequiredItemCount;
        batch.RejectedItemCount = rejectedItemCount;
        batch.FailureCode = failureCode;
        return batch;
    }

    private static void ValidateRestoredDecisionCounts(
        ImportBatchState state,
        int expectedItemCount,
        int acceptedItemCount,
        int reviewRequiredItemCount,
        int rejectedItemCount)
    {
        if (acceptedItemCount < 0 || reviewRequiredItemCount < 0 || rejectedItemCount < 0)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_COUNTS_INVALID",
                "Persisted item decision counts cannot be negative.");
        }

        var decidedItemCount = acceptedItemCount + reviewRequiredItemCount + rejectedItemCount;
        if (decidedItemCount > expectedItemCount)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_COUNTS_INVALID",
                "Persisted item decision counts exceed the exact registered package size.");
        }

        if (state is ImportBatchState.Registered
            or ImportBatchState.Uploading
            or ImportBatchState.Uploaded
            or ImportBatchState.IntegrityChecking
            or ImportBatchState.IntegrityValid
            or ImportBatchState.ItemValidation
            or ImportBatchState.IntegrityFailed
            or ImportBatchState.ContractRejected
            or ImportBatchState.BlockedByPolicy)
        {
            if (decidedItemCount != 0)
            {
                throw new IngestionDomainException(
                    "INGESTION_DECISION_COUNTS_INVALID",
                    $"State '{state}' cannot contain completed item decisions.");
            }

            return;
        }

        if (state is ImportBatchState.ReviewRequired
            or ImportBatchState.ReadyToCommit
            or ImportBatchState.Committing
            or ImportBatchState.Committed
            or ImportBatchState.PartiallyRejected
            or ImportBatchState.Superseded
            or ImportBatchState.CommitFailed)
        {
            if (decidedItemCount != expectedItemCount)
            {
                throw new IngestionDomainException(
                    "INGESTION_DECISION_COUNTS_INVALID",
                    $"State '{state}' requires decisions covering the exact registered package.");
            }
        }

        if (state == ImportBatchState.ReviewRequired && reviewRequiredItemCount == 0)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_COUNTS_INVALID",
                "Review-required state must contain at least one item awaiting review.");
        }

        if (state is ImportBatchState.ReadyToCommit
            or ImportBatchState.Committing
            or ImportBatchState.Committed
            or ImportBatchState.PartiallyRejected
            or ImportBatchState.Superseded
            or ImportBatchState.CommitFailed && reviewRequiredItemCount != 0)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_COUNTS_INVALID",
                $"State '{state}' cannot contain unresolved review-required items.");
        }

        if (state == ImportBatchState.Committed &&
            (acceptedItemCount != expectedItemCount || rejectedItemCount != 0))
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_COUNTS_INVALID",
                "Committed state requires every registered item to be delivered.");
        }

        if (state == ImportBatchState.PartiallyRejected && rejectedItemCount == 0)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_COUNTS_INVALID",
                "Partially rejected state requires at least one explicit rejection.");
        }
    }

    private static void ValidateRestoredFailure(ImportBatchState state, string? failureCode)
    {
        var requiresFailure = state is ImportBatchState.IntegrityFailed
            or ImportBatchState.ContractRejected
            or ImportBatchState.BlockedByPolicy
            or ImportBatchState.CommitFailed;
        if (requiresFailure)
        {
            IngestionContractRules.RequireSemanticKey(
                failureCode ?? throw new IngestionDomainException(
                    "INGESTION_FAILURE_CODE_REQUIRED",
                    $"State '{state}' requires a failure code."),
                nameof(failureCode));
            return;
        }

        if (failureCode is not null)
        {
            throw new IngestionDomainException(
                "INGESTION_FAILURE_CODE_INVALID",
                $"State '{state}' cannot retain a failure code.");
        }
    }

    private static long MinimumAggregateRevision(ImportBatchState state) => state switch
    {
        ImportBatchState.Registered => 1,
        ImportBatchState.Uploading => 2,
        ImportBatchState.Uploaded => 3,
        ImportBatchState.IntegrityChecking => 4,
        ImportBatchState.IntegrityValid => 5,
        ImportBatchState.ItemValidation => 6,
        ImportBatchState.ReviewRequired or ImportBatchState.ReadyToCommit => 7,
        ImportBatchState.Committing => 8,
        ImportBatchState.Committed or ImportBatchState.PartiallyRejected or ImportBatchState.CommitFailed => 9,
        ImportBatchState.Superseded => 10,
        ImportBatchState.IntegrityFailed or ImportBatchState.ContractRejected or ImportBatchState.BlockedByPolicy => 5,
        ImportBatchState.Expired or ImportBatchState.Cancelled => 2,
        _ => throw new IngestionDomainException(
            "INGESTION_BATCH_STATE_INVALID",
            $"Persisted import batch state '{state}' is unsupported."),
    };
}
