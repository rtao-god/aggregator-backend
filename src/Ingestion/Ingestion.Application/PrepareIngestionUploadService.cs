using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public sealed record PrepareIngestionUploadCommand(
    Guid BatchId,
    long ExpectedAggregateRevision,
    string IdempotencyKey,
    string CallerServiceIdentity);

public sealed class PrepareIngestionUploadService
{
    private static readonly TimeSpan UploadLifetime = TimeSpan.FromMinutes(10);

    private readonly IIngestionBatchRepository _batchRepository;
    private readonly IIngestionBatchLifecycleRepository _lifecycleRepository;
    private readonly IIngestionPayloadStore _payloadStore;
    private readonly IIngestionClock _clock;

    public PrepareIngestionUploadService(
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

    public async Task<IngestionUploadAuthorizationDto> PrepareAsync(
        PrepareIngestionUploadCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateCommand(command.BatchId, command.ExpectedAggregateRevision, command.CallerServiceIdentity);
        var commandIdentity = CreateCommandIdentity(command);
        var replay = await _lifecycleRepository.ReadCommandResultAsync(
            commandIdentity,
            cancellationToken);
        if (replay is not null)
        {
            EnsureReplay(replay, command.BatchId, ImportBatchState.Uploading);
            return await CreateAuthorizationAsync(replay, replayed: true, cancellationToken);
        }

        var snapshot = await _batchRepository.ReadAsync(
            ImportBatchId.Create(command.BatchId),
            cancellationToken)
            ?? throw BatchNotFound(command.BatchId);
        var batch = snapshot.ToDomain();
        batch.BeginUpload(command.ExpectedAggregateRevision, GetUtcNow());
        var saved = await _lifecycleRepository.SaveLifecycleAsync(
            batch,
            snapshot.AggregateRevision,
            commandIdentity,
            command.CallerServiceIdentity,
            cancellationToken);
        EnsureReplay(saved.Batch, command.BatchId, ImportBatchState.Uploading);
        return await CreateAuthorizationAsync(saved.Batch, saved.Replayed, cancellationToken);
    }

    private async Task<IngestionUploadAuthorizationDto> CreateAuthorizationAsync(
        IngestionBatchSnapshot batch,
        bool replayed,
        CancellationToken cancellationToken)
    {
        var authorization = await _payloadStore.CreateUploadAuthorizationAsync(
            batch.PayloadObjectKey,
            batch.PayloadContentType,
            batch.PayloadObjectSize,
            UploadLifetime,
            cancellationToken);
        return new IngestionUploadAuthorizationDto(
            authorization.UploadUri,
            authorization.ObjectKey,
            authorization.ExpiresAtUtc,
            authorization.ContentType,
            authorization.MaximumSize,
            IngestionBatchContractMapper.ToDto(batch),
            replayed);
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
        PrepareIngestionUploadCommand command)
    {
        var requestDigest = IngestionCanonicalJson.ComputeDigest(new
        {
            command.BatchId,
            command.ExpectedAggregateRevision,
            command.CallerServiceIdentity,
        });
        return IngestionCommandIdentity.Create(
            $"ingestion.batch.prepare-upload:{command.BatchId:D}",
            command.IdempotencyKey,
            requestDigest);
    }

    private static void ValidateCommand(
        Guid batchId,
        long expectedAggregateRevision,
        string callerServiceIdentity)
    {
        if (batchId == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_BATCH_ID_REQUIRED",
                400,
                "A non-empty import batch ID is required.",
                "Use the exact ImportBatchId returned by registration.");
        }

        if (expectedAggregateRevision <= 0)
        {
            throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_EXPECTED_REVISION_REQUIRED",
                400,
                "A positive expected aggregate revision is required.",
                "Read the current import batch and retry with its aggregate revision.");
        }

        if (string.IsNullOrWhiteSpace(callerServiceIdentity) || callerServiceIdentity.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Access",
                "INGESTION_SERVICE_IDENTITY_REQUIRED",
                403,
                "A valid caller service identity is required.",
                "Authenticate with the dedicated collector workload identity.");
        }
    }

    private static void EnsureReplay(
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
                "The persisted command result does not match its upload operation contract.",
                "Restore the exact idempotency result from a verified database backup.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["expectedBatchId"] = expectedBatchId,
                    ["actualBatchId"] = batch.Id.Value,
                    ["expectedState"] = expectedState.ToString(),
                    ["actualState"] = batch.State.ToString(),
                });
        }
    }

    private static IngestionApplicationException BatchNotFound(Guid batchId) =>
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
}
