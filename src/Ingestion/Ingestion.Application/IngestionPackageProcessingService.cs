using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public sealed record IngestionPackageProcessingOptions
{
    public int BatchSize { get; init; } = 10;

    public long MaximumPayloadBytes { get; init; } = 64L * 1024 * 1024;

    public TimeSpan LeaseLifetime { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan EmptyPollDelay { get; init; } = TimeSpan.FromSeconds(2);

    public void Validate()
    {
        if (BatchSize is < 1 or > 100)
        {
            throw new InvalidOperationException("Ingestion package batch size must be between 1 and 100.");
        }

        if (MaximumPayloadBytes is < 1 or > 512L * 1024 * 1024)
        {
            throw new InvalidOperationException(
                "Ingestion maximum package size must be between one byte and 512 MiB.");
        }

        if (LeaseLifetime < TimeSpan.FromSeconds(30) || LeaseLifetime > TimeSpan.FromMinutes(30))
        {
            throw new InvalidOperationException(
                "Ingestion package lease lifetime must be between 30 seconds and 30 minutes.");
        }

        if (EmptyPollDelay < TimeSpan.FromMilliseconds(100) || EmptyPollDelay > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException(
                "Ingestion empty poll delay must be between 100 milliseconds and one minute.");
        }
    }
}

public sealed record IngestionPackageWorkClaim(
    Guid ClaimId,
    int AttemptNumber,
    string WorkerIdentity,
    DateTimeOffset LeasedUntilUtc,
    IngestionBatchSnapshot Batch);

public enum IngestionPackageProcessOutcome
{
    NoWork = 1,
    Completed = 2,
    IntegrityRejected = 3,
}

public sealed record IngestionPackageProcessResult(
    IngestionPackageProcessOutcome Outcome,
    Guid? BatchId,
    string? FailureCode);

public interface IIngestionPackageObjectReader
{
    public Task<byte[]> ReadExactAsync(
        string objectKey,
        string expectedDigest,
        long expectedSize,
        long maximumSize,
        CancellationToken cancellationToken);
}

public interface IIngestionPackageWorkRepository
{
    public Task<IngestionPackageWorkClaim?> ClaimNextAsync(
        string workerIdentity,
        DateTimeOffset leasedAtUtc,
        TimeSpan leaseLifetime,
        CancellationToken cancellationToken);

    public Task CompleteAsync(
        IngestionPackageWorkClaim claim,
        ImportBatch batch,
        IngestionPackageValidationResult validation,
        CancellationToken cancellationToken);

    public Task FailIntegrityAsync(
        IngestionPackageWorkClaim claim,
        ImportBatch batch,
        string failureCode,
        CancellationToken cancellationToken);
}

/// <summary>Processes at most one durable package claim and never performs a partial package commit.</summary>
public sealed class IngestionPackageProcessingService(
    IIngestionPackageWorkRepository workRepository,
    IIngestionPackageObjectReader objectReader,
    IngestionPackagePayloadValidator validator,
    IIngestionClock clock,
    IngestionPackageProcessingOptions options)
{
    private readonly IIngestionPackageWorkRepository _workRepository =
        workRepository ?? throw new ArgumentNullException(nameof(workRepository));
    private readonly IIngestionPackageObjectReader _objectReader =
        objectReader ?? throw new ArgumentNullException(nameof(objectReader));
    private readonly IngestionPackagePayloadValidator _validator =
        validator ?? throw new ArgumentNullException(nameof(validator));
    private readonly IIngestionClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly IngestionPackageProcessingOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));

    public async Task<IngestionPackageProcessResult> ProcessNextAsync(
        string workerIdentity,
        CancellationToken cancellationToken)
    {
        _options.Validate();
        if (string.IsNullOrWhiteSpace(workerIdentity) || workerIdentity.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Worker",
                "INGESTION_WORKER_IDENTITY_INVALID",
                500,
                "The package worker identity is missing or invalid.",
                "Configure one stable worker identity before starting package processing.");
        }

        var now = _clock.GetUtcNow();
        var claim = await _workRepository.ClaimNextAsync(
            workerIdentity.Trim(),
            now,
            _options.LeaseLifetime,
            cancellationToken);
        if (claim is null)
        {
            return new IngestionPackageProcessResult(
                IngestionPackageProcessOutcome.NoWork,
                BatchId: null,
                FailureCode: null);
        }

        try
        {
            var bytes = await _objectReader.ReadExactAsync(
                claim.Batch.PayloadObjectKey,
                claim.Batch.PayloadObjectDigest,
                claim.Batch.PayloadObjectSize,
                _options.MaximumPayloadBytes,
                cancellationToken);
            var validation = _validator.Validate(claim.Batch, bytes);
            var batch = Restore(claim.Batch);
            var changedAtUtc = _clock.GetUtcNow();
            batch.MarkIntegrityValid(batch.AggregateRevision, changedAtUtc);
            batch.BeginItemValidation(batch.AggregateRevision, changedAtUtc);
            batch.CompleteItemValidation(
                validation.AcceptedItemCount,
                validation.ReviewRequiredItemCount,
                validation.RejectedItemCount,
                batch.AggregateRevision,
                changedAtUtc);
            await _workRepository.CompleteAsync(
                claim,
                batch,
                validation,
                cancellationToken);
            return new IngestionPackageProcessResult(
                IngestionPackageProcessOutcome.Completed,
                batch.Id.Value,
                FailureCode: null);
        }
        catch (IngestionPackageIntegrityException exception)
        {
            var batch = Restore(claim.Batch);
            batch.RejectIntegrity(
                exception.Code,
                batch.AggregateRevision,
                _clock.GetUtcNow());
            await _workRepository.FailIntegrityAsync(
                claim,
                batch,
                exception.Code,
                cancellationToken);
            return new IngestionPackageProcessResult(
                IngestionPackageProcessOutcome.IntegrityRejected,
                batch.Id.Value,
                exception.Code);
        }
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
