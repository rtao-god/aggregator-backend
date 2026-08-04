namespace Aggregator.Ingestion.Domain;

public enum ImportItemDecisionKind
{
    Accepted = 1,
    NeedsReview = 2,
    Rejected = 3,
    Superseded = 4,
}

/// <summary>Represents one immutable item decision in the Ingestion-owned append-only ledger.</summary>
public sealed record ImportItemDecision
{
    private ImportItemDecision(
        Guid id,
        ImportBatchId batchId,
        string itemKey,
        long sequence,
        ImportItemDecisionKind kind,
        string reasonCode,
        IngestionActorId decidedBy,
        DateTimeOffset decidedAtUtc,
        Guid? supersedesDecisionId)
    {
        Id = id;
        BatchId = batchId;
        ItemKey = itemKey;
        Sequence = sequence;
        Kind = kind;
        ReasonCode = reasonCode;
        DecidedBy = decidedBy;
        DecidedAtUtc = decidedAtUtc;
        SupersedesDecisionId = supersedesDecisionId;
    }

    public Guid Id { get; }

    public ImportBatchId BatchId { get; }

    public string ItemKey { get; }

    public long Sequence { get; }

    public ImportItemDecisionKind Kind { get; }

    public string ReasonCode { get; }

    public IngestionActorId DecidedBy { get; }

    public DateTimeOffset DecidedAtUtc { get; }

    public Guid? SupersedesDecisionId { get; }

    public static ImportItemDecision Create(
        Guid id,
        ImportBatchId batchId,
        string itemKey,
        long sequence,
        ImportItemDecisionKind kind,
        string reasonCode,
        IngestionActorId decidedBy,
        DateTimeOffset decidedAtUtc,
        Guid? supersedesDecisionId = null)
    {
        IngestionContractRules.RequireId(id, nameof(id));
        IngestionContractRules.RequireId(batchId.Value, nameof(batchId));
        IngestionContractRules.RequireSemanticKey(itemKey, nameof(itemKey));
        if (sequence <= 0)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_SEQUENCE_INVALID",
                "An item decision sequence must be positive.");
        }

        if (!Enum.IsDefined(kind))
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_KIND_INVALID",
                $"Item decision kind '{kind}' is unsupported.");
        }

        IngestionContractRules.RequireSemanticKey(reasonCode, nameof(reasonCode));
        IngestionContractRules.RequireId(decidedBy.Value, nameof(decidedBy));
        IngestionContractRules.RequireUtc(decidedAtUtc, nameof(decidedAtUtc));
        if (sequence == 1 && supersedesDecisionId is not null)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_SUPERSESSION_INVALID",
                "The first item decision cannot supersede another decision.");
        }

        if (sequence > 1 && supersedesDecisionId is null)
        {
            throw new IngestionDomainException(
                "INGESTION_DECISION_SUPERSESSION_REQUIRED",
                "A later item decision must reference the exact decision it supersedes.");
        }

        if (supersedesDecisionId is { } supersededId)
        {
            IngestionContractRules.RequireId(supersededId, nameof(supersedesDecisionId));
        }

        return new ImportItemDecision(
            id,
            batchId,
            itemKey,
            sequence,
            kind,
            reasonCode,
            decidedBy,
            decidedAtUtc,
            supersedesDecisionId);
    }
}
