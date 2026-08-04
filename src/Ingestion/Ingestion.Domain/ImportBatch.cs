namespace Aggregator.Ingestion.Domain;

public enum ImportBatchState
{
    Registered = 1,
    Uploading = 2,
    Uploaded = 3,
    IntegrityChecking = 4,
    IntegrityValid = 5,
    ItemValidation = 6,
    ReviewRequired = 7,
    ReadyToCommit = 8,
    Committing = 9,
    Committed = 10,
    PartiallyRejected = 11,
    Superseded = 12,
    IntegrityFailed = 13,
    ContractRejected = 14,
    BlockedByPolicy = 15,
    CommitFailed = 16,
    Expired = 17,
    Cancelled = 18,
}

/// <summary>Owns the durable lifecycle of one exact collector package after registration.</summary>
public sealed partial class ImportBatch
{
    private ImportBatch(
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
        DateTimeOffset registeredAtUtc)
    {
        Id = id;
        ProducerIdentity = producerIdentity;
        ProducerBuild = producerBuild;
        CollectorExportId = collectorExportId;
        CollectorExportDigest = collectorExportDigest;
        TargetSiteKey = targetSiteKey;
        TargetCatalogKey = targetCatalogKey;
        TargetCatalogConfigurationRevisionId = targetCatalogConfigurationRevisionId;
        ExpectedItemCount = expectedItemCount;
        ManifestDigest = manifestDigest;
        ItemIndexDigest = itemIndexDigest;
        PayloadDigest = payloadDigest;
        PayloadObjectKey = payloadObjectKey;
        PayloadObjectDigest = payloadObjectDigest;
        PayloadObjectSize = payloadObjectSize;
        PayloadContentType = payloadContentType;
        RegisteredAtUtc = registeredAtUtc;
        LastChangedAtUtc = registeredAtUtc;
        State = ImportBatchState.Registered;
        AggregateRevision = 1;
    }

    public ImportBatchId Id { get; }

    public string ProducerIdentity { get; }

    public string ProducerBuild { get; }

    public Guid CollectorExportId { get; }

    public string CollectorExportDigest { get; }

    public string TargetSiteKey { get; }

    public string TargetCatalogKey { get; }

    public Guid TargetCatalogConfigurationRevisionId { get; }

    public int ExpectedItemCount { get; }

    public string ManifestDigest { get; }

    public string ItemIndexDigest { get; }

    public string PayloadDigest { get; }

    public string PayloadObjectKey { get; }

    public string PayloadObjectDigest { get; }

    public long PayloadObjectSize { get; }

    public string PayloadContentType { get; }

    public DateTimeOffset RegisteredAtUtc { get; }

    public DateTimeOffset LastChangedAtUtc { get; private set; }

    public ImportBatchState State { get; private set; }

    public long AggregateRevision { get; private set; }

    public int AcceptedItemCount { get; private set; }

    public int ReviewRequiredItemCount { get; private set; }

    public int RejectedItemCount { get; private set; }

    public string? FailureCode { get; private set; }

    public static ImportBatch Create(
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
        DateTimeOffset registeredAtUtc)
    {
        IngestionContractRules.RequireId(id.Value, nameof(id));
        IngestionContractRules.RequireText(producerIdentity, nameof(producerIdentity), 200);
        IngestionContractRules.RequireText(producerBuild, nameof(producerBuild), 200);
        IngestionContractRules.RequireId(collectorExportId, nameof(collectorExportId));
        IngestionContractRules.RequireDigest(collectorExportDigest, nameof(collectorExportDigest));
        IngestionContractRules.RequireProductKey(targetSiteKey, nameof(targetSiteKey));
        IngestionContractRules.RequireProductKey(targetCatalogKey, nameof(targetCatalogKey));
        IngestionContractRules.RequireId(
            targetCatalogConfigurationRevisionId,
            nameof(targetCatalogConfigurationRevisionId));
        if (expectedItemCount <= 0)
        {
            throw new IngestionDomainException(
                "INGESTION_ITEM_COUNT_INVALID",
                "An import batch must declare at least one item.");
        }

        IngestionContractRules.RequireDigest(manifestDigest, nameof(manifestDigest));
        IngestionContractRules.RequireDigest(itemIndexDigest, nameof(itemIndexDigest));
        IngestionContractRules.RequireDigest(payloadDigest, nameof(payloadDigest));
        IngestionContractRules.RequireText(payloadObjectKey, nameof(payloadObjectKey), 1024);
        IngestionContractRules.RequireDigest(payloadObjectDigest, nameof(payloadObjectDigest));
        if (payloadObjectSize <= 0)
        {
            throw new IngestionDomainException(
                "INGESTION_PAYLOAD_SIZE_INVALID",
                "The registered payload object size must be positive.");
        }

        IngestionContractRules.RequireText(payloadContentType, nameof(payloadContentType), 200);
        IngestionContractRules.RequireUtc(registeredAtUtc, nameof(registeredAtUtc));
        return new ImportBatch(
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
    }

    public void BeginUpload(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        Transition(
            ImportBatchState.Registered,
            ImportBatchState.Uploading,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void MarkUploaded(
        string actualObjectDigest,
        long actualObjectSize,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureStateAndRevision(ImportBatchState.Uploading, expectedAggregateRevision);
        IngestionContractRules.RequireDigest(actualObjectDigest, nameof(actualObjectDigest));
        if (!string.Equals(PayloadObjectDigest, actualObjectDigest, StringComparison.Ordinal) ||
            PayloadObjectSize != actualObjectSize)
        {
            throw new IngestionDomainException(
                "INGESTION_UPLOADED_OBJECT_MISMATCH",
                "The uploaded payload object does not match the registered digest and size.");
        }

        ApplyState(ImportBatchState.Uploaded, changedAtUtc);
    }

    public void BeginIntegrityCheck(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        Transition(
            ImportBatchState.Uploaded,
            ImportBatchState.IntegrityChecking,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void MarkIntegrityValid(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        Transition(
            ImportBatchState.IntegrityChecking,
            ImportBatchState.IntegrityValid,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void RejectIntegrity(
        string failureCode,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        FailFromIntegrityCheck(
            ImportBatchState.IntegrityFailed,
            failureCode,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void RejectContract(
        string failureCode,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        FailFromIntegrityCheck(
            ImportBatchState.ContractRejected,
            failureCode,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void BlockByPolicy(
        string failureCode,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        FailFromIntegrityCheck(
            ImportBatchState.BlockedByPolicy,
            failureCode,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void BeginItemValidation(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        Transition(
            ImportBatchState.IntegrityValid,
            ImportBatchState.ItemValidation,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void CompleteItemValidation(
        int acceptedItemCount,
        int reviewRequiredItemCount,
        int rejectedItemCount,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureStateAndRevision(ImportBatchState.ItemValidation, expectedAggregateRevision);
        ValidateDecisionCounts(acceptedItemCount, reviewRequiredItemCount, rejectedItemCount);
        AcceptedItemCount = acceptedItemCount;
        ReviewRequiredItemCount = reviewRequiredItemCount;
        RejectedItemCount = rejectedItemCount;
        ApplyState(
            reviewRequiredItemCount == 0
                ? ImportBatchState.ReadyToCommit
                : ImportBatchState.ReviewRequired,
            changedAtUtc);
    }

    public void CompleteReview(
        int acceptedItemCount,
        int rejectedItemCount,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureStateAndRevision(ImportBatchState.ReviewRequired, expectedAggregateRevision);
        ValidateDecisionCounts(acceptedItemCount, 0, rejectedItemCount);
        AcceptedItemCount = acceptedItemCount;
        ReviewRequiredItemCount = 0;
        RejectedItemCount = rejectedItemCount;
        ApplyState(ImportBatchState.ReadyToCommit, changedAtUtc);
    }

    public void BeginCommit(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        Transition(
            ImportBatchState.ReadyToCommit,
            ImportBatchState.Committing,
            expectedAggregateRevision,
            changedAtUtc);
    }

    public void CompleteCommit(
        int deliveredItemCount,
        int rejectedItemCount,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureStateAndRevision(ImportBatchState.Committing, expectedAggregateRevision);
        if (deliveredItemCount < 0 || rejectedItemCount < 0 ||
            deliveredItemCount + rejectedItemCount != ExpectedItemCount)
        {
            throw new IngestionDomainException(
                "INGESTION_COMMIT_COUNTS_INVALID",
                "Delivered and rejected item counts must cover the exact registered package.");
        }

        AcceptedItemCount = deliveredItemCount;
        ReviewRequiredItemCount = 0;
        RejectedItemCount = rejectedItemCount;
        ApplyState(
            rejectedItemCount == 0
                ? ImportBatchState.Committed
                : ImportBatchState.PartiallyRejected,
            changedAtUtc);
    }

    public void MarkCommitFailed(
        string failureCode,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureStateAndRevision(ImportBatchState.Committing, expectedAggregateRevision);
        FailureCode = IngestionContractRules.RequireSemanticKey(failureCode, nameof(failureCode));
        ApplyState(ImportBatchState.CommitFailed, changedAtUtc);
    }

    public void Cancel(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        EnsureRevision(expectedAggregateRevision);
        if (IsTerminal(State))
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_TERMINAL",
                $"Import batch state '{State}' cannot be cancelled.");
        }

        ApplyState(ImportBatchState.Cancelled, changedAtUtc);
    }

    public void Expire(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State is ImportBatchState.Committing or ImportBatchState.Committed or ImportBatchState.PartiallyRejected ||
            IsTerminal(State))
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_EXPIRY_INVALID",
                $"Import batch state '{State}' cannot expire.");
        }

        ApplyState(ImportBatchState.Expired, changedAtUtc);
    }

    public void Supersede(long expectedAggregateRevision, DateTimeOffset changedAtUtc)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State is not ImportBatchState.Committed and not ImportBatchState.PartiallyRejected)
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_SUPERSEDE_INVALID",
                "Only a completed import batch can be superseded.");
        }

        ApplyState(ImportBatchState.Superseded, changedAtUtc);
    }

    private void FailFromIntegrityCheck(
        ImportBatchState failureState,
        string failureCode,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureStateAndRevision(ImportBatchState.IntegrityChecking, expectedAggregateRevision);
        FailureCode = IngestionContractRules.RequireSemanticKey(failureCode, nameof(failureCode));
        ApplyState(failureState, changedAtUtc);
    }

    private void ValidateDecisionCounts(int acceptedItemCount, int reviewRequiredItemCount, int rejectedItemCount)
    {
        if (acceptedItemCount < 0 || reviewRequiredItemCount < 0 || rejectedItemCount < 0 ||
            acceptedItemCount + reviewRequiredItemCount + rejectedItemCount != ExpectedItemCount)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_COUNTS_INVALID",
                "Item decision counts must be non-negative and cover the exact registered package.");
        }
    }

    private void Transition(
        ImportBatchState expectedState,
        ImportBatchState nextState,
        long expectedAggregateRevision,
        DateTimeOffset changedAtUtc)
    {
        EnsureStateAndRevision(expectedState, expectedAggregateRevision);
        ApplyState(nextState, changedAtUtc);
    }

    private void EnsureStateAndRevision(ImportBatchState expectedState, long expectedAggregateRevision)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State != expectedState)
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_STATE_INVALID",
                $"Import batch state '{State}' cannot execute a transition requiring '{expectedState}'.");
        }
    }

    private void EnsureRevision(long expectedAggregateRevision)
    {
        if (expectedAggregateRevision != AggregateRevision)
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_REVISION_CONFLICT",
                $"Expected import batch revision {expectedAggregateRevision}, actual revision {AggregateRevision}.");
        }
    }

    private void ApplyState(ImportBatchState state, DateTimeOffset changedAtUtc)
    {
        IngestionContractRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < LastChangedAtUtc)
        {
            throw new IngestionDomainException(
                "INGESTION_BATCH_TIME_REGRESSION",
                "Import batch state cannot move to an earlier timestamp.");
        }

        State = state;
        LastChangedAtUtc = changedAtUtc;
        AggregateRevision++;
    }

    private static bool IsTerminal(ImportBatchState state) =>
        state is ImportBatchState.Superseded
            or ImportBatchState.IntegrityFailed
            or ImportBatchState.ContractRejected
            or ImportBatchState.BlockedByPolicy
            or ImportBatchState.CommitFailed
            or ImportBatchState.Expired
            or ImportBatchState.Cancelled;
}
