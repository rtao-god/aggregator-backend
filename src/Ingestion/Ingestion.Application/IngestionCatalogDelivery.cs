using Aggregator.Catalog.Contracts;

namespace Aggregator.Ingestion.Application;

/// <summary>Exclusive lease over one exact Ingestion-owned Catalog command delivery.</summary>
public sealed record IngestionCatalogDeliveryLease(
    Guid DeliveryId,
    Guid BatchId,
    string ItemKey,
    Guid LeaseToken,
    DateTimeOffset LeaseExpiresAtUtc,
    CatalogIngestionUpsertDraftCommand Command,
    string CommandDigest,
    int AttemptCount);

/// <summary>Catalog outcome bound to the exact Ingestion delivery lease that received it.</summary>
public sealed record IngestionCatalogDeliveryResult(
    Guid DeliveryId,
    Guid BatchId,
    string ItemKey,
    Guid LeaseToken,
    CatalogIngestionCommandOutcome Outcome);

/// <summary>Typed transport or owner failure bound to one exact Catalog delivery lease.</summary>
public sealed record IngestionCatalogDeliveryFailure(
    Guid DeliveryId,
    Guid BatchId,
    string ItemKey,
    Guid LeaseToken,
    string FailureCode,
    string FailureDetail);

/// <summary>Retry classification produced by the Ingestion Catalog delivery adapter.</summary>
public sealed record IngestionCatalogDeliveryFailureDecision(
    bool Retry,
    DateTimeOffset? NextAttemptAtUtc,
    string FailureCode,
    string FailureDetail);

/// <summary>Signals that a worker no longer owns the Catalog delivery it attempted to mutate.</summary>
public sealed class IngestionCatalogDeliveryLeaseLostException : InvalidOperationException
{
    public IngestionCatalogDeliveryLeaseLostException(Guid deliveryId)
        : base($"Ingestion Catalog delivery '{deliveryId:D}' lease is absent, expired, or owned by another worker.")
    {
        if (deliveryId == Guid.Empty)
        {
            throw new ArgumentException("Delivery ID is required.", nameof(deliveryId));
        }

        DeliveryId = deliveryId;
    }

    public Guid DeliveryId { get; }
}

/// <summary>Canonical persistence boundary for Catalog command delivery and outcome tracking.</summary>
public interface IIngestionCatalogDeliveryStore
{
    public Task<IReadOnlyList<IngestionCatalogDeliveryLease>> LeaseAsync(
        string workerIdentity,
        int limit,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc,
        CancellationToken cancellationToken);

    public Task<IngestionProcessingSnapshot> RecordOutcomeAsync(
        IngestionCatalogDeliveryResult result,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken);

    public Task ScheduleRetryAsync(
        IngestionCatalogDeliveryFailure failure,
        DateTimeOffset nextAttemptAtUtc,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);

    public Task<IngestionProcessingSnapshot> FailAsync(
        IngestionCatalogDeliveryFailure failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Authenticated Catalog command boundary consumed by Ingestion.</summary>
public interface IIngestionCatalogCommandClient
{
    public Task<CatalogIngestionCommandOutcome> SendAsync(
        CatalogIngestionUpsertDraftCommand command,
        CancellationToken cancellationToken);
}

/// <summary>Classifies delivery failures without weakening Catalog or Ingestion contracts.</summary>
public interface IIngestionCatalogDeliveryFailureClassifier
{
    public IngestionCatalogDeliveryFailureDecision Classify(
        Exception exception,
        int attempt,
        int maximumAttempts,
        DateTimeOffset failedAtUtc);
}

/// <summary>Executes durable Ingestion-to-Catalog commands under exact database leases.</summary>
public sealed class ProcessIngestionCatalogDeliveriesService(
    IIngestionCatalogDeliveryStore store,
    IIngestionCatalogCommandClient client,
    IIngestionCatalogDeliveryFailureClassifier failureClassifier,
    TimeProvider timeProvider)
{
    public async Task<int> ProcessAsync(
        string workerIdentity,
        int limit,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ValidateOptions(workerIdentity, limit, leaseDuration, maximumAttempts);
        var leasedAtUtc = RequireUtc(timeProvider.GetUtcNow());
        var deliveries = await store.LeaseAsync(
            workerIdentity.Trim(),
            limit,
            leasedAtUtc,
            leasedAtUtc.Add(leaseDuration),
            cancellationToken);
        var processed = 0;
        foreach (var delivery in deliveries)
        {
            if (delivery.AttemptCount > maximumAttempts)
            {
                await FailAttemptLimitAsync(delivery, maximumAttempts, cancellationToken);
                processed++;
                continue;
            }

            try
            {
                var outcome = await client.SendAsync(delivery.Command, cancellationToken);
                await store.RecordOutcomeAsync(
                    new IngestionCatalogDeliveryResult(
                        delivery.DeliveryId,
                        delivery.BatchId,
                        delivery.ItemKey,
                        delivery.LeaseToken,
                        outcome),
                    RequireUtc(timeProvider.GetUtcNow()),
                    cancellationToken);
            }
            catch (IngestionCatalogDeliveryLeaseLostException)
            {
                // A replacement attempt owns the delivery; this worker must not mutate it.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await RecordFailureAsync(
                    delivery,
                    exception,
                    maximumAttempts,
                    cancellationToken);
            }

            processed++;
        }

        return processed;
    }

    private async Task RecordFailureAsync(
        IngestionCatalogDeliveryLease delivery,
        Exception exception,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        var failedAtUtc = RequireUtc(timeProvider.GetUtcNow());
        var decision = failureClassifier.Classify(
            exception,
            delivery.AttemptCount,
            maximumAttempts,
            failedAtUtc);
        ValidateFailureDecision(decision, failedAtUtc);
        var failure = new IngestionCatalogDeliveryFailure(
            delivery.DeliveryId,
            delivery.BatchId,
            delivery.ItemKey,
            delivery.LeaseToken,
            decision.FailureCode,
            decision.FailureDetail);
        try
        {
            if (decision.Retry)
            {
                await store.ScheduleRetryAsync(
                    failure,
                    decision.NextAttemptAtUtc!.Value,
                    failedAtUtc,
                    cancellationToken);
            }
            else
            {
                await store.FailAsync(failure, failedAtUtc, cancellationToken);
            }
        }
        catch (IngestionCatalogDeliveryLeaseLostException)
        {
            // A replacement attempt owns the delivery; retain its state unchanged.
        }
    }

    private async Task FailAttemptLimitAsync(
        IngestionCatalogDeliveryLease delivery,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        try
        {
            await store.FailAsync(
                new IngestionCatalogDeliveryFailure(
                    delivery.DeliveryId,
                    delivery.BatchId,
                    delivery.ItemKey,
                    delivery.LeaseToken,
                    "INGESTION_CATALOG_DELIVERY_ATTEMPT_LIMIT_EXCEEDED",
                    $"Catalog delivery exceeded the configured maximum of {maximumAttempts} attempts."),
                RequireUtc(timeProvider.GetUtcNow()),
                cancellationToken);
        }
        catch (IngestionCatalogDeliveryLeaseLostException)
        {
            // A replacement attempt owns the delivery; retain its state unchanged.
        }
    }

    private static void ValidateOptions(
        string workerIdentity,
        int limit,
        TimeSpan leaseDuration,
        int maximumAttempts)
    {
        if (string.IsNullOrWhiteSpace(workerIdentity) ||
            workerIdentity.Length > 200 ||
            workerIdentity.Any(char.IsControl))
        {
            throw Failure(
                "INGESTION_DELIVERY_WORKER_INVALID",
                "A bounded delivery worker identity is required.");
        }

        if (limit is < 1 or > 1_000 ||
            leaseDuration < TimeSpan.FromSeconds(10) ||
            leaseDuration > TimeSpan.FromMinutes(15) ||
            maximumAttempts is < 1 or > 100)
        {
            throw Failure(
                "INGESTION_DELIVERY_OPTIONS_INVALID",
                "The delivery batch, lease duration, or attempt limit is outside supported bounds.");
        }
    }

    private static void ValidateFailureDecision(
        IngestionCatalogDeliveryFailureDecision decision,
        DateTimeOffset failedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (string.IsNullOrWhiteSpace(decision.FailureCode) ||
            decision.FailureCode.Length > 200 ||
            string.IsNullOrWhiteSpace(decision.FailureDetail) ||
            decision.FailureDetail.Length > 4_000 ||
            decision.Retry != decision.NextAttemptAtUtc.HasValue ||
            decision.NextAttemptAtUtc is { } nextAttempt &&
            (nextAttempt.Offset != TimeSpan.Zero || nextAttempt <= failedAtUtc))
        {
            throw Failure(
                "INGESTION_DELIVERY_FAILURE_DECISION_INVALID",
                "The Catalog delivery failure classifier returned an invalid decision.");
        }
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "INGESTION_DELIVERY_CLOCK_NOT_UTC",
                "The Ingestion delivery clock returned a non-UTC timestamp.");
        }

        return value;
    }

    private static IngestionApplicationException Failure(string code, string detail) =>
        new(
            "Ingestion.Delivery",
            code,
            500,
            detail,
            "Correct the Ingestion Catalog delivery owner before resuming the worker.");
}
