using System.Security.Cryptography;
using System.Text.Json;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Application;

public sealed record LeasedIngestionProcessingBatch(
    IngestionBatchSnapshot Batch,
    string WorkerIdentity,
    DateTimeOffset LeaseExpiresAtUtc);

public sealed record IngestionProcessingDecision(
    Guid DecisionId,
    string ItemKey,
    string ItemDigest,
    IngestionProcessingDecisionContract Decision,
    IReadOnlyList<string> ReasonCodes,
    Guid? SupersedesDecisionId,
    DateTimeOffset DecidedAtUtc,
    string DecidedBy,
    IngestionCandidatePayloadItem Item);

public sealed record IngestionProcessingSnapshot(
    IngestionBatchSnapshot Batch,
    IReadOnlyList<IngestionProcessingDecision> Decisions);

public sealed record PendingIngestionCatalogDelivery(
    Guid DeliveryId,
    Guid BatchId,
    string ItemKey,
    CatalogIngestionUpsertDraftCommand Command,
    string CommandDigest,
    int AttemptCount);

public sealed record IngestionCatalogDeliveryOutcome(
    Guid DeliveryId,
    Guid BatchId,
    string ItemKey,
    CatalogIngestionCommandOutcome Outcome);

public interface IIngestionProcessingStore
{
    public Task<LeasedIngestionProcessingBatch?> LeaseNextUploadedAsync(
        string workerIdentity,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    public Task<IngestionProcessingSnapshot> CompleteValidationAsync(
        Guid batchId,
        long expectedAggregateRevision,
        string payloadDigest,
        IReadOnlyList<IngestionProcessingDecision> decisions,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    public Task FailValidationAsync(
        Guid batchId,
        long expectedAggregateRevision,
        string failureCode,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);

    public Task<IngestionProcessingSnapshot?> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken);

    public Task<IngestionProcessingSnapshot> CompleteReviewAsync(
        Guid batchId,
        long expectedAggregateRevision,
        IReadOnlyList<ReviewIngestionItemRequest> reviewDecisions,
        string reviewerIdentity,
        DateTimeOffset reviewedAtUtc,
        CancellationToken cancellationToken);

    public Task<IngestionCommitResult> BeginCommitAsync(
        Guid batchId,
        long expectedAggregateRevision,
        IngestionCommandIdentity commandIdentity,
        string callerIdentity,
        DateTimeOffset requestedAtUtc,
        CancellationToken cancellationToken);

    public Task<IReadOnlyList<PendingIngestionCatalogDelivery>> LeaseCatalogDeliveriesAsync(
        string workerIdentity,
        int limit,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    public Task<IngestionProcessingSnapshot> RecordCatalogOutcomeAsync(
        IngestionCatalogDeliveryOutcome outcome,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);
}

public sealed record IngestionCommitResult(
    IngestionProcessingSnapshot Processing,
    IReadOnlyList<IngestionCatalogDeliveryDto> Deliveries,
    bool Replayed);

public interface IIngestionProcessingPayloadReader
{
    public Task<Stream> OpenVerifiedAsync(
        string objectKey,
        string expectedDigest,
        long expectedSize,
        string expectedContentType,
        CancellationToken cancellationToken);
}

public interface IIngestionCatalogCommandPublisher
{
    public Task PublishAsync(
        CatalogIngestionUpsertDraftCommand command,
        CancellationToken cancellationToken);
}

public sealed class ValidateIngestionPackageService(
    IIngestionProcessingStore store,
    IIngestionProcessingPayloadReader payloadReader,
    TimeProvider timeProvider)
{
    private const long MaximumPayloadBytes = 128L * 1024 * 1024;

    public async Task<bool> ProcessNextAsync(
        string workerIdentity,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        ValidateWorker(workerIdentity, leaseDuration);
        var now = timeProvider.GetUtcNow();
        var lease = await store.LeaseNextUploadedAsync(
            workerIdentity,
            now,
            now + leaseDuration,
            cancellationToken);
        if (lease is null)
        {
            return false;
        }

        var batch = lease.Batch;
        try
        {
            if (batch.PayloadObjectSize > MaximumPayloadBytes)
            {
                throw ValidationFailure(
                    "INGESTION_PAYLOAD_TOO_LARGE",
                    "The payload exceeds the bounded processing limit.");
            }

            await using var stream = await payloadReader.OpenVerifiedAsync(
                batch.PayloadObjectKey,
                batch.PayloadObjectDigest,
                batch.PayloadObjectSize,
                batch.PayloadContentType,
                cancellationToken);
            var payloadBytes = await ReadBoundedAsync(
                stream,
                batch.PayloadObjectSize,
                cancellationToken);
            var actualDigest = IngestionCanonicalJson.ComputeDigest(payloadBytes);
            if (!string.Equals(actualDigest, batch.PayloadObjectDigest, StringComparison.Ordinal))
            {
                throw ValidationFailure(
                    "INGESTION_PAYLOAD_DIGEST_MISMATCH",
                    "The verified payload bytes do not match the registered object digest.");
            }

            var payload = IngestionCanonicalJson.Deserialize<IngestionCandidatePayloadDocument>(payloadBytes);
            ValidatePayloadIdentity(batch, payload);
            var decisions = ClassifyItems(payload.Items, now, workerIdentity);
            await store.CompleteValidationAsync(
                batch.Id.Value,
                batch.AggregateRevision,
                actualDigest,
                decisions,
                now,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failureCode = exception switch
            {
                IngestionApplicationException application => application.Code,
                IngestionDomainException domain => domain.Code,
                JsonException => "INGESTION_PAYLOAD_JSON_INVALID",
                InvalidDataException => "INGESTION_PAYLOAD_STORAGE_INVALID",
                _ => "INGESTION_VALIDATION_UNHANDLED",
            };
            await store.FailValidationAsync(
                batch.Id.Value,
                batch.AggregateRevision,
                failureCode,
                timeProvider.GetUtcNow(),
                cancellationToken);
            if (failureCode == "INGESTION_VALIDATION_UNHANDLED")
            {
                throw;
            }

            return true;
        }
    }

    private static IReadOnlyList<IngestionProcessingDecision> ClassifyItems(
        IReadOnlyList<IngestionCandidatePayloadItem> items,
        DateTimeOffset decidedAtUtc,
        string decidedBy)
    {
        if (items.Count == 0)
        {
            throw ValidationFailure(
                "INGESTION_PAYLOAD_ITEMS_EMPTY",
                "The candidate payload must contain at least one item.");
        }

        var duplicateKeys = items
            .GroupBy(item => item.ItemKey, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (duplicateKeys.Length > 0)
        {
            throw new IngestionApplicationException(
                "Ingestion.Validation",
                "INGESTION_ITEM_KEY_DUPLICATE",
                422,
                "The candidate payload contains duplicate item keys.",
                "Regenerate the complete sealed package with unique item identities.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["duplicateItemKeys"] = duplicateKeys,
                });
        }

        return items
            .OrderBy(item => item.ItemKey, StringComparer.Ordinal)
            .Select(item => ClassifyItem(item, decidedAtUtc, decidedBy))
            .ToArray();
    }

    private static IngestionProcessingDecision ClassifyItem(
        IngestionCandidatePayloadItem item,
        DateTimeOffset decidedAtUtc,
        string decidedBy)
    {
        var blocking = new SortedSet<string>(StringComparer.Ordinal);
        var review = new SortedSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(item.ItemKey) || item.ItemKey.Length > 200)
        {
            blocking.Add("item_key_invalid");
        }

        if (item.EntityKind is not ("place" or "provider"))
        {
            blocking.Add("entity_kind_unsupported");
        }

        if (string.IsNullOrWhiteSpace(item.SubjectNaturalKey) || item.SubjectNaturalKey.Length > 300)
        {
            blocking.Add("subject_natural_key_invalid");
        }

        if (item.Fields.Count == 0)
        {
            blocking.Add("fields_empty");
        }

        foreach (var duplicate in item.Fields
                     .GroupBy(field => (field.FieldKey, field.Locale), FieldIdentityComparer.Instance)
                     .Where(group => group.Count() > 1))
        {
            blocking.Add($"field_duplicate:{duplicate.Key.FieldKey}:{duplicate.Key.Locale}");
        }

        foreach (var field in item.Fields)
        {
            ValidateField(field, blocking, review);
        }

        var itemDocument = IngestionCanonicalJson.Serialize(item);
        var itemDigest = IngestionCanonicalJson.ComputeDigest(itemDocument);
        var decision = blocking.Count > 0
            ? IngestionProcessingDecisionContract.Rejected
            : review.Count > 0
                ? IngestionProcessingDecisionContract.NeedsReview
                : IngestionProcessingDecisionContract.Accepted;
        IReadOnlyList<string> reasons = blocking.Count > 0
            ? blocking.ToArray()
            : review.ToArray();
        return new IngestionProcessingDecision(
            Guid.CreateVersion7(),
            item.ItemKey,
            itemDigest,
            decision,
            reasons,
            SupersedesDecisionId: null,
            decidedAtUtc,
            decidedBy,
            item);
    }

    private static void ValidateField(
        IngestionCandidateFieldContract field,
        ISet<string> blocking,
        ISet<string> review)
    {
        if (string.IsNullOrWhiteSpace(field.FieldKey) || field.FieldKey.Length > 96)
        {
            blocking.Add("field_key_invalid");
        }

        if (!Enum.IsDefined(field.Kind))
        {
            blocking.Add($"field_kind_invalid:{field.FieldKey}");
        }

        if (string.IsNullOrWhiteSpace(field.CanonicalValue) || field.CanonicalValue.Length > 10_000)
        {
            blocking.Add($"field_value_invalid:{field.FieldKey}");
        }

        if (string.IsNullOrWhiteSpace(field.Locale) || field.Locale.Length > 35)
        {
            blocking.Add($"field_locale_invalid:{field.FieldKey}");
        }

        if (string.IsNullOrWhiteSpace(field.SourceKey) || field.SourceKey.Length > 96)
        {
            blocking.Add($"field_source_invalid:{field.FieldKey}");
        }

        if (!IsDigest(field.EvidenceDigest))
        {
            blocking.Add($"field_evidence_digest_invalid:{field.FieldKey}");
        }

        switch (field.UsagePolicy)
        {
            case "public_allowed":
                break;
            case "link_only" when field.Kind == IngestionCandidateFieldValueKindContract.ExternalReference:
                break;
            case "internal_review_only":
                review.Add($"field_internal_review:{field.FieldKey}");
                break;
            case "research_only":
                blocking.Add($"field_research_only:{field.FieldKey}");
                break;
            case "forbidden":
                blocking.Add($"field_forbidden:{field.FieldKey}");
                break;
            case "link_only":
                blocking.Add($"field_link_only_kind_invalid:{field.FieldKey}");
                break;
            default:
                blocking.Add($"field_usage_policy_invalid:{field.FieldKey}");
                break;
        }
    }

    private static void ValidatePayloadIdentity(
        IngestionBatchSnapshot batch,
        IngestionCandidatePayloadDocument payload)
    {
        if (!string.Equals(
                payload.ContractIdentity,
                IngestionCandidatePayloadContract.Identity,
                StringComparison.Ordinal) ||
            payload.ContractRevision != IngestionCandidatePayloadContract.Revision)
        {
            throw ValidationFailure(
                "INGESTION_PAYLOAD_CONTRACT_UNSUPPORTED",
                "The candidate payload contract identity or revision is unsupported.");
        }

        if (payload.CollectorExportId != batch.CollectorExportId ||
            !string.Equals(
                payload.CollectorExportDigest,
                batch.CollectorExportDigest,
                StringComparison.Ordinal))
        {
            throw ValidationFailure(
                "INGESTION_PAYLOAD_EXPORT_IDENTITY_MISMATCH",
                "The payload identifies a different sealed collector export.");
        }

        if (payload.Items.Count != batch.ExpectedItemCount)
        {
            throw ValidationFailure(
                "INGESTION_PAYLOAD_ITEM_COUNT_MISMATCH",
                "The payload item count does not match the registered manifest.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        if (!stream.CanRead)
        {
            throw new InvalidDataException("The verified payload stream is not readable.");
        }

        using var buffer = expectedSize > int.MaxValue
            ? new MemoryStream()
            : new MemoryStream((int)expectedSize);
        await stream.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"The verified payload size changed during read. Expected {expectedSize}, actual {buffer.Length}.");
        }

        return buffer.ToArray();
    }

    private static void ValidateWorker(string workerIdentity, TimeSpan leaseDuration)
    {
        if (string.IsNullOrWhiteSpace(workerIdentity) || workerIdentity.Length > 200)
        {
            throw ValidationFailure(
                "INGESTION_WORKER_IDENTITY_INVALID",
                "A bounded validation worker identity is required.");
        }

        if (leaseDuration < TimeSpan.FromSeconds(10) || leaseDuration > TimeSpan.FromMinutes(15))
        {
            throw ValidationFailure(
                "INGESTION_VALIDATION_LEASE_INVALID",
                "The validation lease must be between ten seconds and fifteen minutes.");
        }
    }

    private static bool IsDigest(string value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IngestionApplicationException ValidationFailure(string code, string detail) =>
        new(
            "Ingestion.Validation",
            code,
            422,
            detail,
            "Regenerate the complete sealed package from the documented producer contract.");

    private sealed class FieldIdentityComparer : IEqualityComparer<(string FieldKey, string Locale)>
    {
        public static FieldIdentityComparer Instance { get; } = new();

        public bool Equals(
            (string FieldKey, string Locale) left,
            (string FieldKey, string Locale) right) =>
            string.Equals(left.FieldKey, right.FieldKey, StringComparison.Ordinal) &&
            string.Equals(left.Locale, right.Locale, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string FieldKey, string Locale) value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.FieldKey),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Locale));
    }
}

public sealed class ReviewIngestionPackageService(
    IIngestionProcessingStore store,
    TimeProvider timeProvider)
{
    public async Task<IngestionBatchProcessingResponse> CompleteAsync(
        Guid batchId,
        CompleteIngestionReviewRequest request,
        string reviewerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (batchId == Guid.Empty)
        {
            throw ContractFailure("INGESTION_BATCH_ID_REQUIRED", "A batch ID is required.");
        }

        if (string.IsNullOrWhiteSpace(reviewerIdentity) || reviewerIdentity.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Access",
                "INGESTION_REVIEWER_IDENTITY_REQUIRED",
                403,
                "A valid reviewer identity is required.",
                "Authenticate with an internal reviewer workload identity.");
        }

        if (request.Decisions.Count == 0 ||
            request.Decisions.Select(decision => decision.ItemKey).Distinct(StringComparer.Ordinal).Count() != request.Decisions.Count)
        {
            throw ContractFailure(
                "INGESTION_REVIEW_DECISIONS_INVALID",
                "Review decisions must be non-empty and contain each item key at most once.");
        }

        var result = await store.CompleteReviewAsync(
            batchId,
            request.ExpectedAggregateRevision,
            request.Decisions,
            reviewerIdentity,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return ToResponse(result);
    }

    internal static IngestionBatchProcessingResponse ToResponse(IngestionProcessingSnapshot result) =>
        new(
            result.Batch.Id.Value,
            result.Batch.State.ToString(),
            result.Batch.AggregateRevision,
            result.Batch.ExpectedItemCount,
            result.Batch.AcceptedItemCount,
            result.Batch.ReviewRequiredItemCount,
            result.Batch.RejectedItemCount,
            result.Decisions
                .OrderBy(decision => decision.ItemKey, StringComparer.Ordinal)
                .Select(ToDto)
                .ToArray());

    internal static IngestionProcessingItemDecisionDto ToDto(IngestionProcessingDecision decision) =>
        new(
            decision.DecisionId,
            decision.ItemKey,
            decision.ItemDigest,
            decision.Decision,
            decision.ReasonCodes,
            decision.SupersedesDecisionId,
            decision.DecidedAtUtc,
            decision.DecidedBy);

    private static IngestionApplicationException ContractFailure(string code, string detail) =>
        new(
            "Ingestion.Contracts",
            code,
            400,
            detail,
            "Correct the review request and retry with the current aggregate revision.");
}

public sealed class CommitIngestionPackageService(
    IIngestionProcessingStore store,
    TimeProvider timeProvider)
{
    public Task<IngestionCommitResult> BeginAsync(
        Guid batchId,
        CommitIngestionBatchRequest request,
        string idempotencyKey,
        string callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (batchId == Guid.Empty)
        {
            throw ContractFailure("INGESTION_BATCH_ID_REQUIRED", "A batch ID is required.");
        }

        var hashInput = new
        {
            batchId,
            request.ExpectedAggregateRevision,
        };
        var identity = IngestionCommandIdentity.Create(
            "ingestion.batch.commit",
            idempotencyKey,
            IngestionCanonicalJson.ComputeDigest(hashInput));
        return store.BeginCommitAsync(
            batchId,
            request.ExpectedAggregateRevision,
            identity,
            callerIdentity,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }

    private static IngestionApplicationException ContractFailure(string code, string detail) =>
        new(
            "Ingestion.Contracts",
            code,
            400,
            detail,
            "Correct the commit request and retry with the current aggregate revision.");
}

public sealed class DeliverIngestionCatalogCommandsService(
    IIngestionProcessingStore store,
    IIngestionCatalogCommandPublisher publisher,
    TimeProvider timeProvider)
{
    public async Task<int> DeliverAsync(
        string workerIdentity,
        int limit,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerIdentity) || workerIdentity.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Delivery",
                "INGESTION_DELIVERY_WORKER_INVALID",
                500,
                "A bounded delivery worker identity is required.",
                "Correct the worker configuration.");
        }

        if (limit is < 1 or > 1_000 ||
            leaseDuration < TimeSpan.FromSeconds(10) ||
            leaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new IngestionApplicationException(
                "Ingestion.Delivery",
                "INGESTION_DELIVERY_LEASE_INVALID",
                500,
                "The delivery batch or lease duration is outside the supported bounds.",
                "Correct the worker configuration.");
        }

        var now = timeProvider.GetUtcNow();
        var deliveries = await store.LeaseCatalogDeliveriesAsync(
            workerIdentity,
            limit,
            now,
            now + leaseDuration,
            cancellationToken);
        var delivered = 0;
        foreach (var delivery in deliveries)
        {
            try
            {
                await publisher.PublishAsync(delivery.Command, cancellationToken);
                delivered++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        return delivered;
    }

    public Task RecordOutcomeAsync(
        IngestionCatalogDeliveryOutcome outcome,
        CancellationToken cancellationToken) =>
        store.RecordCatalogOutcomeAsync(
            outcome,
            timeProvider.GetUtcNow(),
            cancellationToken);
}

public sealed class ReadIngestionProcessingService(IIngestionProcessingStore store)
{
    public async Task<IngestionBatchProcessingResponse> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        var result = await store.ReadAsync(batchId, cancellationToken)
            ?? throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_BATCH_NOT_FOUND",
                404,
                $"Import batch '{batchId:D}' was not found.",
                "Use the exact ImportBatchId returned by registration.");
        return ReviewIngestionPackageService.ToResponse(result);
    }
}

public static class IngestionProcessingApplicationExtensions
{
    public static IServiceCollection AddIngestionProcessingApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ValidateIngestionPackageService>();
        services.AddScoped<ReviewIngestionPackageService>();
        services.AddScoped<CommitIngestionPackageService>();
        services.AddScoped<DeliverIngestionCatalogCommandsService>();
        services.AddScoped<ReadIngestionProcessingService>();
        return services;
    }
}
