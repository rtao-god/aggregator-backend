using System.Text.RegularExpressions;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public sealed record IngestionReviewResolution(
    string ItemKey,
    IngestionItemDecisionContract Decision,
    IReadOnlyList<string> ReasonCodes);

public sealed record IngestionCatalogDeliveryOutcome(
    string ItemKey,
    Guid CommandId,
    IngestionCatalogDeliveryOutcomeContract Outcome,
    Guid? CatalogSubjectId,
    Guid? CatalogListingId,
    Guid? CatalogListingRevisionId,
    string? FailureCode);

public interface IIngestionReviewCommitRepository
{
    public Task<IngestionBatchSnapshot?> ReadCommandResultAsync(
        IngestionCommandIdentity commandIdentity,
        CancellationToken cancellationToken);

    public Task<IngestionBatchCommandResult> CompleteReviewAsync(
        ImportBatch batch,
        long expectedStoredAggregateRevision,
        IReadOnlyList<IngestionReviewResolution> resolutions,
        IngestionCommandIdentity commandIdentity,
        string callerIdentity,
        CancellationToken cancellationToken);

    public Task<IngestionBatchCommandResult> BeginCommitAsync(
        ImportBatch batch,
        long expectedStoredAggregateRevision,
        IReadOnlyList<string> selectedItemKeys,
        IngestionCommandIdentity commandIdentity,
        string callerIdentity,
        CancellationToken cancellationToken);

    public Task<IngestionBatchCommandResult> CompleteCommitAsync(
        ImportBatch batch,
        long expectedStoredAggregateRevision,
        IReadOnlyList<IngestionCatalogDeliveryOutcome> outcomes,
        IngestionCommandIdentity commandIdentity,
        string callerIdentity,
        CancellationToken cancellationToken);
}

public sealed record CompleteIngestionReviewCommand(
    Guid BatchId,
    long ExpectedAggregateRevision,
    IReadOnlyList<IngestionReviewResolution> Resolutions,
    string IdempotencyKey,
    string CallerIdentity);

public sealed record BeginIngestionCommitCommand(
    Guid BatchId,
    long ExpectedAggregateRevision,
    IReadOnlyList<string> SelectedItemKeys,
    string IdempotencyKey,
    string CallerIdentity);

public sealed record CompleteIngestionCommitCommand(
    Guid BatchId,
    long ExpectedAggregateRevision,
    IReadOnlyList<IngestionCatalogDeliveryOutcome> Outcomes,
    string IdempotencyKey,
    string CallerIdentity);

public sealed class CompleteIngestionReviewService(
    IIngestionBatchRepository batchRepository,
    IIngestionReviewCommitRepository workflowRepository,
    IIngestionClock clock)
{
    public async Task<IngestionBatchCommandResult> CompleteAsync(
        CompleteIngestionReviewCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batchId = IngestionReviewCommitRules.RequireBatchId(command.BatchId);
        var resolutions = IngestionReviewCommitRules.NormalizeResolutions(command.Resolutions);
        var identity = IngestionReviewCommitRules.CreateIdentity(
            "ingestion.review.complete",
            batchId.Value,
            command.IdempotencyKey,
            new
            {
                batchId = batchId.Value,
                command.ExpectedAggregateRevision,
                resolutions,
            });
        var replay = await workflowRepository.ReadCommandResultAsync(identity, cancellationToken);
        if (replay is not null)
        {
            return new IngestionBatchCommandResult(replay, true);
        }

        var snapshot = await batchRepository.ReadAsync(batchId, cancellationToken)
            ?? throw IngestionReviewCommitRules.BatchNotFound(batchId.Value);
        if (snapshot.State != ImportBatchState.ReviewRequired)
        {
            throw IngestionReviewCommitRules.StateConflict(
                snapshot,
                ImportBatchState.ReviewRequired,
                "complete review");
        }

        if (snapshot.AggregateRevision != command.ExpectedAggregateRevision)
        {
            throw IngestionReviewCommitRules.RevisionConflict(snapshot, command.ExpectedAggregateRevision);
        }

        if (resolutions.Count != snapshot.ReviewRequiredItemCount)
        {
            throw new IngestionApplicationException(
                "Ingestion.Review",
                "INGESTION_REVIEW_COVERAGE_INVALID",
                422,
                "The review command must resolve every item currently awaiting review exactly once.",
                "Submit one accepted or rejected resolution for each unresolved item.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batchId.Value,
                    ["expectedResolutionCount"] = snapshot.ReviewRequiredItemCount,
                    ["actualResolutionCount"] = resolutions.Count,
                });
        }

        var accepted = resolutions.Count(item => item.Decision == IngestionItemDecisionContract.Accepted);
        var rejected = resolutions.Count - accepted;
        var batch = IngestionReviewCommitRules.Restore(snapshot);
        batch.CompleteReview(
            snapshot.AcceptedItemCount + accepted,
            snapshot.RejectedItemCount + rejected,
            command.ExpectedAggregateRevision,
            clock.GetUtcNow());
        return await workflowRepository.CompleteReviewAsync(
            batch,
            command.ExpectedAggregateRevision,
            resolutions,
            identity,
            IngestionReviewCommitRules.RequireCaller(command.CallerIdentity),
            cancellationToken);
    }
}

public sealed class BeginIngestionCommitService(
    IIngestionBatchRepository batchRepository,
    IIngestionReviewCommitRepository workflowRepository,
    IIngestionClock clock)
{
    public async Task<IngestionBatchCommandResult> BeginAsync(
        BeginIngestionCommitCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batchId = IngestionReviewCommitRules.RequireBatchId(command.BatchId);
        var selectedItemKeys = IngestionReviewCommitRules.NormalizeItemKeys(command.SelectedItemKeys);
        var identity = IngestionReviewCommitRules.CreateIdentity(
            "ingestion.commit.begin",
            batchId.Value,
            command.IdempotencyKey,
            new
            {
                batchId = batchId.Value,
                command.ExpectedAggregateRevision,
                selectedItemKeys,
            });
        var replay = await workflowRepository.ReadCommandResultAsync(identity, cancellationToken);
        if (replay is not null)
        {
            return new IngestionBatchCommandResult(replay, true);
        }

        var snapshot = await batchRepository.ReadAsync(batchId, cancellationToken)
            ?? throw IngestionReviewCommitRules.BatchNotFound(batchId.Value);
        if (snapshot.State != ImportBatchState.ReadyToCommit)
        {
            throw IngestionReviewCommitRules.StateConflict(
                snapshot,
                ImportBatchState.ReadyToCommit,
                "begin commit");
        }

        if (snapshot.AggregateRevision != command.ExpectedAggregateRevision)
        {
            throw IngestionReviewCommitRules.RevisionConflict(snapshot, command.ExpectedAggregateRevision);
        }

        if (selectedItemKeys.Count != snapshot.AcceptedItemCount)
        {
            throw new IngestionApplicationException(
                "Ingestion.Commit",
                "INGESTION_COMMIT_SELECTION_INVALID",
                422,
                "The commit selection must identify every accepted package item exactly once.",
                "Submit the exact accepted item keys from the current decision projection.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batchId.Value,
                    ["expectedSelectedItemCount"] = snapshot.AcceptedItemCount,
                    ["actualSelectedItemCount"] = selectedItemKeys.Count,
                });
        }

        var batch = IngestionReviewCommitRules.Restore(snapshot);
        batch.BeginCommit(command.ExpectedAggregateRevision, clock.GetUtcNow());
        return await workflowRepository.BeginCommitAsync(
            batch,
            command.ExpectedAggregateRevision,
            selectedItemKeys,
            identity,
            IngestionReviewCommitRules.RequireCaller(command.CallerIdentity),
            cancellationToken);
    }
}

public sealed class CompleteIngestionCommitService(
    IIngestionBatchRepository batchRepository,
    IIngestionReviewCommitRepository workflowRepository,
    IIngestionClock clock)
{
    public async Task<IngestionBatchCommandResult> CompleteAsync(
        CompleteIngestionCommitCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var batchId = IngestionReviewCommitRules.RequireBatchId(command.BatchId);
        var outcomes = IngestionReviewCommitRules.NormalizeOutcomes(command.Outcomes);
        var identity = IngestionReviewCommitRules.CreateIdentity(
            "ingestion.commit.complete",
            batchId.Value,
            command.IdempotencyKey,
            new
            {
                batchId = batchId.Value,
                command.ExpectedAggregateRevision,
                outcomes,
            });
        var replay = await workflowRepository.ReadCommandResultAsync(identity, cancellationToken);
        if (replay is not null)
        {
            return new IngestionBatchCommandResult(replay, true);
        }

        var snapshot = await batchRepository.ReadAsync(batchId, cancellationToken)
            ?? throw IngestionReviewCommitRules.BatchNotFound(batchId.Value);
        if (snapshot.State != ImportBatchState.Committing)
        {
            throw IngestionReviewCommitRules.StateConflict(
                snapshot,
                ImportBatchState.Committing,
                "complete commit");
        }

        if (snapshot.AggregateRevision != command.ExpectedAggregateRevision)
        {
            throw IngestionReviewCommitRules.RevisionConflict(snapshot, command.ExpectedAggregateRevision);
        }

        if (outcomes.Count != snapshot.AcceptedItemCount)
        {
            throw new IngestionApplicationException(
                "Ingestion.Commit",
                "INGESTION_CATALOG_OUTCOME_COVERAGE_INVALID",
                422,
                "Catalog outcomes must cover every selected accepted item exactly once.",
                "Record one delivered or rejected Catalog outcome for each selected item.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["batchId"] = batchId.Value,
                    ["expectedOutcomeCount"] = snapshot.AcceptedItemCount,
                    ["actualOutcomeCount"] = outcomes.Count,
                });
        }

        var delivered = outcomes.Count(item => item.Outcome == IngestionCatalogDeliveryOutcomeContract.Delivered);
        var catalogRejected = outcomes.Count - delivered;
        var totalRejected = snapshot.RejectedItemCount + catalogRejected;
        var batch = IngestionReviewCommitRules.Restore(snapshot);
        batch.CompleteCommit(
            delivered,
            totalRejected,
            command.ExpectedAggregateRevision,
            clock.GetUtcNow());
        return await workflowRepository.CompleteCommitAsync(
            batch,
            command.ExpectedAggregateRevision,
            outcomes,
            identity,
            IngestionReviewCommitRules.RequireCaller(command.CallerIdentity),
            cancellationToken);
    }
}

internal static partial class IngestionReviewCommitRules
{
    private static readonly Regex SemanticKeyPattern = SemanticKeyRegex();

    public static ImportBatchId RequireBatchId(Guid batchId)
    {
        if (batchId == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_BATCH_ID_REQUIRED",
                400,
                "A non-empty import batch ID is required.",
                "Use the exact ImportBatchId returned by registration.");
        }

        return ImportBatchId.Create(batchId);
    }

    public static string RequireCaller(string callerIdentity)
    {
        if (string.IsNullOrWhiteSpace(callerIdentity) || callerIdentity.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Access",
                "INGESTION_CALLER_IDENTITY_INVALID",
                403,
                "The authenticated workflow caller identity is invalid.",
                "Authenticate with an exact workload or operator subject.");
        }

        return callerIdentity.Trim();
    }

    public static IReadOnlyList<IngestionReviewResolution> NormalizeResolutions(
        IReadOnlyList<IngestionReviewResolution> resolutions)
    {
        ArgumentNullException.ThrowIfNull(resolutions);
        if (resolutions.Count == 0)
        {
            throw ContractFailure(
                "INGESTION_REVIEW_RESOLUTIONS_REQUIRED",
                "At least one review resolution is required.");
        }

        var normalized = resolutions
            .Select(item =>
            {
                ArgumentNullException.ThrowIfNull(item);
                if (item.Decision is not IngestionItemDecisionContract.Accepted and
                    not IngestionItemDecisionContract.Rejected)
                {
                    throw ContractFailure(
                        "INGESTION_REVIEW_DECISION_INVALID",
                        "A review completion may resolve an item only as accepted or rejected.");
                }

                var reasons = NormalizeReasonCodes(item.ReasonCodes);
                return item with
                {
                    ItemKey = NormalizeItemKey(item.ItemKey),
                    ReasonCodes = reasons,
                };
            })
            .OrderBy(item => item.ItemKey, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(normalized.Select(item => item.ItemKey), "INGESTION_REVIEW_ITEM_DUPLICATE");
        return normalized;
    }

    public static IReadOnlyList<string> NormalizeItemKeys(IReadOnlyList<string> itemKeys)
    {
        ArgumentNullException.ThrowIfNull(itemKeys);
        if (itemKeys.Count == 0)
        {
            throw ContractFailure(
                "INGESTION_COMMIT_SELECTION_REQUIRED",
                "At least one accepted item must be selected for commit.");
        }

        var normalized = itemKeys
            .Select(NormalizeItemKey)
            .Order(StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(normalized, "INGESTION_COMMIT_ITEM_DUPLICATE");
        return normalized;
    }

    public static IReadOnlyList<IngestionCatalogDeliveryOutcome> NormalizeOutcomes(
        IReadOnlyList<IngestionCatalogDeliveryOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        if (outcomes.Count == 0)
        {
            throw ContractFailure(
                "INGESTION_CATALOG_OUTCOMES_REQUIRED",
                "At least one Catalog delivery outcome is required.");
        }

        var normalized = outcomes
            .Select(item =>
            {
                ArgumentNullException.ThrowIfNull(item);
                if (item.CommandId == Guid.Empty)
                {
                    throw ContractFailure(
                        "INGESTION_CATALOG_COMMAND_ID_REQUIRED",
                        "Every Catalog delivery outcome requires a non-empty command ID.");
                }

                if (!Enum.IsDefined(item.Outcome))
                {
                    throw ContractFailure(
                        "INGESTION_CATALOG_OUTCOME_INVALID",
                        "The Catalog delivery outcome is unsupported.");
                }

                if (item.Outcome == IngestionCatalogDeliveryOutcomeContract.Delivered &&
                    (item.CatalogSubjectId is null || item.CatalogSubjectId == Guid.Empty ||
                     item.CatalogListingId is null || item.CatalogListingId == Guid.Empty ||
                     item.CatalogListingRevisionId is null || item.CatalogListingRevisionId == Guid.Empty ||
                     item.FailureCode is not null))
                {
                    throw ContractFailure(
                        "INGESTION_CATALOG_DELIVERY_IDENTITY_INVALID",
                        "A delivered outcome requires exact non-empty Catalog subject, listing and revision IDs and no failure code.");
                }

                if (item.Outcome == IngestionCatalogDeliveryOutcomeContract.Rejected &&
                    (item.CatalogSubjectId is not null || item.CatalogListingId is not null ||
                     item.CatalogListingRevisionId is not null || string.IsNullOrWhiteSpace(item.FailureCode)))
                {
                    throw ContractFailure(
                        "INGESTION_CATALOG_REJECTION_INVALID",
                        "A rejected outcome requires one failure code and no Catalog resource IDs.");
                }

                var failureCode = item.FailureCode is null
                    ? null
                    : NormalizeSemanticKey(item.FailureCode, 200);
                return item with
                {
                    ItemKey = NormalizeItemKey(item.ItemKey),
                    FailureCode = failureCode,
                };
            })
            .OrderBy(item => item.ItemKey, StringComparer.Ordinal)
            .ToArray();
        EnsureUnique(normalized.Select(item => item.ItemKey), "INGESTION_CATALOG_OUTCOME_DUPLICATE");
        if (normalized.Select(item => item.CommandId).Distinct().Count() != normalized.Length)
        {
            throw ContractFailure(
                "INGESTION_CATALOG_COMMAND_DUPLICATE",
                "A Catalog command ID may occur only once in one completion request.");
        }

        return normalized;
    }

    public static IngestionCommandIdentity CreateIdentity<T>(
        string scopePrefix,
        Guid batchId,
        string idempotencyKey,
        T request)
    {
        var digest = IngestionCanonicalJson.ComputeDigest(request);
        return IngestionCommandIdentity.Create(
            $"{scopePrefix}:{batchId:D}",
            idempotencyKey,
            digest);
    }

    public static ImportBatch Restore(IngestionBatchSnapshot snapshot) =>
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

    public static IngestionApplicationException BatchNotFound(Guid batchId) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_NOT_FOUND",
            404,
            $"Import batch '{batchId:D}' was not found.",
            "Use the exact ImportBatchId returned by registration.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchId"] = batchId,
            });

    public static IngestionApplicationException RevisionConflict(
        IngestionBatchSnapshot snapshot,
        long expectedRevision) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_REVISION_CONFLICT",
            409,
            "The import batch changed before the workflow command was applied.",
            "Reload the exact batch and retry with its current aggregate revision.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchId"] = snapshot.Id.Value,
                ["expectedRevision"] = expectedRevision,
                ["actualRevision"] = snapshot.AggregateRevision,
            });

    public static IngestionApplicationException StateConflict(
        IngestionBatchSnapshot snapshot,
        ImportBatchState expectedState,
        string operation) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_STATE_INVALID",
            409,
            $"Import batch state '{snapshot.State}' cannot {operation}; state '{expectedState}' is required.",
            "Reload the exact batch and execute only the operation allowed by its current state.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchId"] = snapshot.Id.Value,
                ["expectedState"] = expectedState.ToString(),
                ["actualState"] = snapshot.State.ToString(),
            });

    private static IReadOnlyList<string> NormalizeReasonCodes(IReadOnlyList<string> reasonCodes)
    {
        ArgumentNullException.ThrowIfNull(reasonCodes);
        if (reasonCodes.Count == 0)
        {
            throw ContractFailure(
                "INGESTION_REVIEW_REASON_REQUIRED",
                "Every review resolution requires at least one reason code.");
        }

        var normalized = reasonCodes
            .Select(value => NormalizeSemanticKey(value, 200))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalized.Length > 50)
        {
            throw ContractFailure(
                "INGESTION_REVIEW_REASON_COUNT_INVALID",
                "One item resolution cannot contain more than 50 reason codes.");
        }

        return normalized;
    }

    private static string NormalizeItemKey(string itemKey)
    {
        if (string.IsNullOrWhiteSpace(itemKey) || itemKey.Length > 300 || itemKey.Any(char.IsControl))
        {
            throw ContractFailure(
                "INGESTION_ITEM_KEY_INVALID",
                "An item key must be non-empty, contain no control characters and be at most 300 characters.");
        }

        return itemKey.Trim();
    }

    private static string NormalizeSemanticKey(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength ||
            !SemanticKeyPattern.IsMatch(value))
        {
            throw ContractFailure(
                "INGESTION_SEMANTIC_KEY_INVALID",
                "A lowercase semantic key containing letters, digits, dots, underscores or hyphens is required.");
        }

        return value;
    }

    private static void EnsureUnique(IEnumerable<string> values, string code)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!set.Add(value))
            {
                throw ContractFailure(code, $"Item key '{value}' occurs more than once.");
            }
        }
    }

    private static IngestionApplicationException ContractFailure(string code, string detail) =>
        new(
            "Ingestion.Contracts",
            code,
            422,
            detail,
            "Correct the exact workflow request and retry with a new idempotency key.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SemanticKeyRegex();
}
