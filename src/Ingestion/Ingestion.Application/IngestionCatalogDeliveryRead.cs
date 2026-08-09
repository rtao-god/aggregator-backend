using Aggregator.Ingestion.Contracts;

namespace Aggregator.Ingestion.Application;

/// <summary>Canonical persisted lifecycle of one Catalog command delivery.</summary>
public enum IngestionCatalogDeliveryState
{
    Pending = 1,
    Leased = 2,
    Succeeded = 3,
    Rejected = 4,
}

/// <summary>Validated read model of one exact Ingestion-owned Catalog delivery.</summary>
public sealed record IngestionCatalogDeliverySnapshot(
    Guid DeliveryId,
    Guid BatchId,
    string ItemKey,
    string CommandType,
    string CommandDigest,
    IngestionCatalogDeliveryState State,
    int AttemptCount,
    DateTimeOffset? LeaseExpiresAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    Guid? CatalogListingId,
    Guid? CatalogListingRevisionId,
    string? FailureCode,
    string? FailureDetail,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastChangedAtUtc)
{
    public static IngestionCatalogDeliverySnapshot Create(
        Guid deliveryId,
        Guid batchId,
        string itemKey,
        string commandType,
        string commandDigest,
        IngestionCatalogDeliveryState state,
        int attemptCount,
        DateTimeOffset? leaseExpiresAtUtc,
        DateTimeOffset? nextAttemptAtUtc,
        Guid? catalogListingId,
        Guid? catalogListingRevisionId,
        string? failureCode,
        string? failureDetail,
        DateTimeOffset createdAtUtc,
        DateTimeOffset lastChangedAtUtc)
    {
        RequireIdentifier(deliveryId, nameof(deliveryId));
        RequireIdentifier(batchId, nameof(batchId));
        var normalizedItemKey = RequireText(itemKey, nameof(itemKey), 200);
        var normalizedCommandType = RequireText(commandType, nameof(commandType), 200);
        var normalizedCommandDigest = RequireDigest(commandDigest, nameof(commandDigest));
        if (!Enum.IsDefined(state))
        {
            throw Invalid("INGESTION_DELIVERY_STATE_INVALID", $"Delivery state '{state}' is unsupported.");
        }

        if (attemptCount < 0)
        {
            throw Invalid("INGESTION_DELIVERY_ATTEMPT_INVALID", "Delivery attempt count cannot be negative.");
        }

        RequireUtc(createdAtUtc, nameof(createdAtUtc));
        RequireUtc(lastChangedAtUtc, nameof(lastChangedAtUtc));
        if (lastChangedAtUtc < createdAtUtc)
        {
            throw Invalid(
                "INGESTION_DELIVERY_TIME_INVALID",
                "Delivery last-changed time cannot precede creation time.");
        }

        RequireOptionalUtc(leaseExpiresAtUtc, nameof(leaseExpiresAtUtc));
        RequireOptionalUtc(nextAttemptAtUtc, nameof(nextAttemptAtUtc));
        var normalizedFailureCode = NormalizeOptional(failureCode, nameof(failureCode), 200);
        var normalizedFailureDetail = NormalizeOptional(failureDetail, nameof(failureDetail), 4_000);
        if ((normalizedFailureCode is null) != (normalizedFailureDetail is null))
        {
            throw Invalid(
                "INGESTION_DELIVERY_FAILURE_TUPLE_INVALID",
                "Delivery failure code and detail must either both be present or both be absent.");
        }

        ValidateStateShape(
            state,
            attemptCount,
            leaseExpiresAtUtc,
            nextAttemptAtUtc,
            catalogListingId,
            catalogListingRevisionId,
            normalizedFailureCode,
            lastChangedAtUtc);
        return new IngestionCatalogDeliverySnapshot(
            deliveryId,
            batchId,
            normalizedItemKey,
            normalizedCommandType,
            normalizedCommandDigest,
            state,
            attemptCount,
            leaseExpiresAtUtc,
            nextAttemptAtUtc,
            catalogListingId,
            catalogListingRevisionId,
            normalizedFailureCode,
            normalizedFailureDetail,
            createdAtUtc,
            lastChangedAtUtc);
    }

    private static void ValidateStateShape(
        IngestionCatalogDeliveryState state,
        int attemptCount,
        DateTimeOffset? leaseExpiresAtUtc,
        DateTimeOffset? nextAttemptAtUtc,
        Guid? listingId,
        Guid? listingRevisionId,
        string? failureCode,
        DateTimeOffset lastChangedAtUtc)
    {
        switch (state)
        {
            case IngestionCatalogDeliveryState.Pending:
                RequireNoLeaseOrResult(leaseExpiresAtUtc, listingId, listingRevisionId, state);
                if (attemptCount == 0)
                {
                    if (nextAttemptAtUtc is not null || failureCode is not null)
                    {
                        throw Invalid(
                            "INGESTION_DELIVERY_PENDING_INITIAL_INVALID",
                            "An unattempted delivery cannot contain retry or failure state.");
                    }
                }
                else if (nextAttemptAtUtc is null ||
                         nextAttemptAtUtc <= lastChangedAtUtc ||
                         failureCode is null)
                {
                    throw Invalid(
                        "INGESTION_DELIVERY_RETRY_STATE_INVALID",
                        "A retried pending delivery requires a future next-attempt time and retained failure.");
                }

                break;
            case IngestionCatalogDeliveryState.Leased:
                if (attemptCount == 0 ||
                    leaseExpiresAtUtc is null ||
                    leaseExpiresAtUtc <= lastChangedAtUtc ||
                    nextAttemptAtUtc is not null ||
                    listingId is not null ||
                    listingRevisionId is not null)
                {
                    throw Invalid(
                        "INGESTION_DELIVERY_LEASE_STATE_INVALID",
                        "A leased delivery requires an active lease and cannot contain a result or retry schedule.");
                }

                break;
            case IngestionCatalogDeliveryState.Succeeded:
                if (attemptCount == 0 ||
                    leaseExpiresAtUtc is not null ||
                    nextAttemptAtUtc is not null ||
                    listingId is null or { } && listingId == Guid.Empty ||
                    listingRevisionId is null or { } && listingRevisionId == Guid.Empty ||
                    failureCode is not null)
                {
                    throw Invalid(
                        "INGESTION_DELIVERY_SUCCESS_STATE_INVALID",
                        "A successful delivery requires exact Catalog listing identities and no active failure state.");
                }

                break;
            case IngestionCatalogDeliveryState.Rejected:
                if (leaseExpiresAtUtc is not null ||
                    nextAttemptAtUtc is not null ||
                    listingId is not null ||
                    listingRevisionId is not null ||
                    failureCode is null)
                {
                    throw Invalid(
                        "INGESTION_DELIVERY_REJECTED_STATE_INVALID",
                        "A rejected delivery requires a retained failure and cannot contain a lease or Catalog result.");
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, "Delivery state is unsupported.");
        }
    }

    private static void RequireNoLeaseOrResult(
        DateTimeOffset? leaseExpiresAtUtc,
        Guid? listingId,
        Guid? listingRevisionId,
        IngestionCatalogDeliveryState state)
    {
        if (leaseExpiresAtUtc is not null || listingId is not null || listingRevisionId is not null)
        {
            throw Invalid(
                "INGESTION_DELIVERY_STATE_SHAPE_INVALID",
                $"Delivery state '{state}' cannot contain a lease or Catalog result identity.");
        }
    }

    private static void RequireIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw Invalid("INGESTION_DELIVERY_IDENTITY_INVALID", $"{parameterName} must be a non-empty UUID.");
        }
    }

    private static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw Invalid(
                "INGESTION_DELIVERY_TEXT_INVALID",
                $"{parameterName} must be non-empty and at most {maximumLength} characters.");
        }

        return value.Trim();
    }

    private static string RequireDigest(string value, string parameterName)
    {
        if (value is not { Length: 64 } ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Invalid(
                "INGESTION_DELIVERY_DIGEST_INVALID",
                $"{parameterName} must be a lowercase SHA-256 digest.");
        }

        return value;
    }

    private static string? NormalizeOptional(string? value, string parameterName, int maximumLength)
    {
        if (value is null)
        {
            return null;
        }

        return RequireText(value, parameterName, maximumLength);
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Invalid("INGESTION_DELIVERY_TIME_NOT_UTC", $"{parameterName} must be normalized to UTC.");
        }
    }

    private static void RequireOptionalUtc(DateTimeOffset? value, string parameterName)
    {
        if (value is { } timestamp)
        {
            RequireUtc(timestamp, parameterName);
        }
    }

    private static IngestionApplicationException Invalid(string code, string detail) =>
        new(
            "Ingestion.Delivery",
            code,
            500,
            detail,
            "Restore the exact delivery ledger from verified Ingestion owner state before serving it.");
}

/// <summary>Complete read-only delivery ledger for one exact import batch.</summary>
public sealed record IngestionCatalogDeliveryCollection(
    Guid BatchId,
    IReadOnlyList<IngestionCatalogDeliverySnapshot> Deliveries);

/// <summary>Read-only persistence boundary for the Ingestion Catalog delivery ledger.</summary>
public interface IIngestionCatalogDeliveryReader
{
    public Task<IngestionCatalogDeliveryCollection?> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken);
}

/// <summary>Reads and maps one exact Ingestion-owned Catalog delivery ledger.</summary>
public sealed class ReadIngestionCatalogDeliveriesService(IIngestionCatalogDeliveryReader reader)
{
    public async Task<IngestionCatalogDeliveriesResponse> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        if (batchId == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Contracts",
                "INGESTION_BATCH_ID_REQUIRED",
                400,
                "A non-empty batch ID is required.",
                "Use the exact ImportBatchId returned by registration.");
        }

        var result = await reader.ReadAsync(batchId, cancellationToken)
            ?? throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_BATCH_NOT_FOUND",
                404,
                $"Import batch '{batchId:D}' was not found.",
                "Use the exact ImportBatchId returned by registration.");
        return new IngestionCatalogDeliveriesResponse(
            result.BatchId,
            result.Deliveries.Select(ToContract).ToArray());
    }

    private static IngestionCatalogDeliveryStatusDto ToContract(IngestionCatalogDeliverySnapshot delivery) =>
        new(
            delivery.DeliveryId,
            delivery.ItemKey,
            delivery.CommandType,
            delivery.CommandDigest,
            delivery.State switch
            {
                IngestionCatalogDeliveryState.Pending => IngestionCatalogDeliveryStateContract.Pending,
                IngestionCatalogDeliveryState.Leased => IngestionCatalogDeliveryStateContract.Leased,
                IngestionCatalogDeliveryState.Succeeded => IngestionCatalogDeliveryStateContract.Succeeded,
                IngestionCatalogDeliveryState.Rejected => IngestionCatalogDeliveryStateContract.Rejected,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(delivery),
                    delivery.State,
                    "Delivery state is unsupported."),
            },
            delivery.AttemptCount,
            delivery.LeaseExpiresAtUtc,
            delivery.NextAttemptAtUtc,
            delivery.CatalogListingId,
            delivery.CatalogListingRevisionId,
            delivery.FailureCode,
            delivery.FailureDetail,
            delivery.CreatedAtUtc,
            delivery.LastChangedAtUtc);
}
