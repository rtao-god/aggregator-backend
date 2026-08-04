using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public sealed record CompleteIngestionUploadCommand(
    Guid BatchId,
    long ExpectedAggregateRevision,
    string IdempotencyKey,
    string CallerServiceIdentity);

public sealed class CompleteIngestionUploadService
{
    private readonly IIngestionBatchRepository _batchRepository;
    private readonly IIngestionBatchLifecycleRepository _lifecycleRepository;
    private readonly IIngestionPayloadStore _payloadStore;
    private readonly IIngestionClock _clock;

    public CompleteIngestionUploadService(
        IIngestionBatchRepository batchRepository,
        IIngestionBatchLifecycleRepository lifecycleRepository,
        IIngestionPayloadStore payloadStore,
        IIngestionClock clock)
    {
        _batchRepository = batchRepository ?? throw new ArgumentNullException(nameof(batchRepository));
        _lifecycleRepository = lifecycleRepository ?? throw new ArgumentNullException(nameof(lifecycleRepository));
        _payloadStore = payloadStore ?? throw new ArgumentNullException(nameof(payloadStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IngestionBatchCommandResponse> CompleteAsync(
        CompleteIngestionUploadCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command);
        var commandIdentity = CreateCommandIdentity(command);
        var replay = await _lifecycleRepository.ReadCommandResultAsync(
            commandIdentity,
            cancellationToken);
        if (replay is not null)
        {
            EnsureResult(replay, command.BatchId, ImportBatchState.Uploaded);
            return new IngestionBatchCommandResponse(
                IngestionBatchContractMapper.ToDto(replay),
                true);
        }

        var snapshot = await _batchRepository.ReadAsync(
            ImportBatchId.Create(command.BatchId),
            cancellationToken)
            ?? throw BatchNotFound(command.BatchId);
        if (snapshot.AggregateRevision != command.ExpectedAggregateRevision)
        {
            throw RevisionConflict(
                command.BatchId,
                command.ExpectedAggregateRevision,
                snapshot.AggregateRevision);
        }

        var descriptor = await _payloadStore.VerifyUploadedAsync(
            snapshot.PayloadObjectKey,
            snapshot.PayloadObjectDigest,
            snapshot.PayloadObjectSize,
            snapshot.PayloadContentType,
            cancellationToken);
        if (!string.Equals(descriptor.ObjectKey, snapshot.PayloadObjectKey, StringComparison.Ordinal) ||
            !string.Equals(
                descriptor.ContentDigest,
                snapshot.PayloadObjectDigest,
                StringComparison.Ordinal) ||
            descriptor.Size != snapshot.PayloadObjectSize ||
            !string.Equals(
                descriptor.ContentType,
                snapshot.PayloadContentType,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_PAYLOAD_VERIFICATION_RESULT_INVALID",
                500,
                "The payload-store adapter returned a descriptor that does not match the registered manifest.",
                "Correct the payload-store adapter before accepting upload completions.");
        }

        var batch = snapshot.ToDomain();
        batch.MarkUploaded(
            descriptor.ContentDigest,
            descriptor.Size,
            command.ExpectedAggregateRevision,
            GetUtcNow());
        var saved = await _lifecycleRepository.SaveLifecycleAsync(
            batch,
            snapshot.AggregateRevision,
            commandIdentity,
            command.CallerServiceIdentity,
            cancellationToken);
        EnsureResult(saved.Batch, command.BatchId, ImportBatchState.Uploaded);
        return new IngestionBatchCommandResponse(
            IngestionBatchContractMapper.ToDto(saved.Batch),
            saved.Replayed);
    }

    private DateTimeOffset GetUtcNow()
    {
        var value = _clock.GetUtcNow();
        if (value.Offset != TimeSpan.Zero)
        {
            throw new IngestionApplicationException(
                "Ingestion.Clock",
                "INGESTION_CLOCK_NOT_UTC",
                500,
                "The Ingestion clock returned a non-UTC timestamp.",
                "Correct the composition root to supply a UTC clock.");
        }

        return value;
    }

    private static IngestionCommandIdentity CreateCommandIdentity(
        CompleteIngestionUploadCommand command)
    {
        var requestDigest = IngestionCanonicalJson.ComputeDigest(new
        {
            command.BatchId,
            command.ExpectedAggregateRevision,
            command.CallerServiceIdentity,
        });
        return IngestionCommandIdentity.Create(
            $"ingestion.batch.complete-upload:{command.BatchId:D}",
            command.IdempotencyKey,
            requestDigest);
    }

    private static void ValidateCommand(CompleteIngestionUploadCommand command)
    {
        if (command.BatchId == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_BATCH_ID_REQUIRED",
                400,
                "A non-empty import batch ID is required.",
                "Use the exact ImportBatchId returned by registration.");
        }

        if (command.ExpectedAggregateRevision <= 0)
        {
            throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_EXPECTED_REVISION_REQUIRED",
                400,
                "A positive expected aggregate revision is required.",
                "Read the current import batch and retry with its aggregate revision.");
        }

        if (string.IsNullOrWhiteSpace(command.CallerServiceIdentity) ||
            command.CallerServiceIdentity.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Access",
                "INGESTION_SERVICE_IDENTITY_REQUIRED",
                403,
                "A valid caller service identity is required.",
                "Authenticate with the dedicated collector workload identity.");
        }
    }

    private static void EnsureResult(
        IngestionBatchSnapshot batch,
        Guid expectedBatchId,
        ImportBatchState expectedState)
    {
        if (batch.Id.Value != expectedBatchId || batch.State != expectedState)
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_COMMAND_RESULT_INVALID",
                500,
                "The persisted command result does not match its upload-completion contract.",
                "Restore the exact idempotency result from a verified database backup.");
        }
    }

    private static IngestionApplicationException BatchNotFound(Guid batchId) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_NOT_FOUND",
            404,
            $"Import batch '{batchId:D}' was not found.",
            "Use the exact ImportBatchId returned by registration.");

    private static IngestionApplicationException RevisionConflict(
        Guid batchId,
        long expectedRevision,
        long actualRevision) =>
        new(
            "Ingestion.Batches",
            "INGESTION_BATCH_REVISION_CONFLICT",
            409,
            "The import batch changed before upload completion.",
            "Reload the exact batch and retry with its current aggregate revision.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["batchId"] = batchId,
                ["expectedRevision"] = expectedRevision,
                ["actualRevision"] = actualRevision,
            });
}
