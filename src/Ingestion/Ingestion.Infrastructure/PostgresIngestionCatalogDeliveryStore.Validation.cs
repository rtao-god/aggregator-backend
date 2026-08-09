using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;

namespace Aggregator.Ingestion.Infrastructure;

public sealed partial class PostgresIngestionCatalogDeliveryStore
{
    private static CatalogIngestionUpsertDraftCommand VerifyCommand(DeliveryCandidate candidate)
    {
        if (!string.Equals(candidate.CommandType, CatalogIngestionCommandContracts.UpsertDraft, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_DELIVERY_COMMAND_TYPE_UNSUPPORTED",
                500,
                $"Catalog delivery '{candidate.DeliveryId:D}' has unsupported command type '{candidate.CommandType}'.",
                "Restore the delivery from a verified Ingestion database backup.");
        }

        var actualDigest = IngestionCanonicalJson.ComputeDigest(candidate.CommandDocument);
        if (!string.Equals(actualDigest, candidate.CommandDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_DELIVERY_COMMAND_DIGEST_MISMATCH",
                500,
                $"Catalog delivery '{candidate.DeliveryId:D}' command document failed digest verification.",
                "Restore the delivery from a verified Ingestion database backup.");
        }

        var command = IngestionCanonicalJson.Deserialize<CatalogIngestionUpsertDraftCommand>(candidate.CommandDocument);
        if (command.CommandId != candidate.DeliveryId ||
            command.IngestionBatchId != candidate.BatchId ||
            !string.Equals(command.IngestionItemKey, candidate.ItemKey, StringComparison.Ordinal) ||
            !string.Equals(CatalogIngestionCommandDigest.Compute(command), command.CommandDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_DELIVERY_COMMAND_IDENTITY_MISMATCH",
                500,
                $"Catalog delivery '{candidate.DeliveryId:D}' command identity is inconsistent.",
                "Restore the delivery from a verified Ingestion database backup.");
        }

        return command;
    }

    private static void ValidateOutcome(CatalogIngestionCommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.CommandId == Guid.Empty ||
            outcome.IngestionBatchId == Guid.Empty ||
            string.IsNullOrWhiteSpace(outcome.IngestionItemKey) ||
            outcome.CompletedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "INGESTION_CATALOG_OUTCOME_INVALID",
                502,
                "Catalog returned an outcome with invalid required identity or time.",
                "Correct the Catalog ingestion response contract before replaying delivery.");
        }

        var success = outcome.State is
            CatalogIngestionOutcomeStateContract.DraftCreated or
            CatalogIngestionOutcomeStateContract.DraftUpdated;
        var validSuccess = success &&
            outcome.ListingId is { } listingId && listingId != Guid.Empty &&
            outcome.ListingRevisionId is { } revisionId && revisionId != Guid.Empty &&
            outcome.FailureCode is null &&
            outcome.FailureDetail is null;
        var validRejection = outcome.State == CatalogIngestionOutcomeStateContract.Rejected &&
            outcome.ListingId is null &&
            outcome.ListingRevisionId is null &&
            !string.IsNullOrWhiteSpace(outcome.FailureCode) &&
            !string.IsNullOrWhiteSpace(outcome.FailureDetail);
        if (!validSuccess && !validRejection)
        {
            throw Failure(
                "INGESTION_CATALOG_OUTCOME_STATE_INVALID",
                502,
                "Catalog returned an internally inconsistent ingestion outcome.",
                "Correct the Catalog ingestion response contract before replaying delivery.");
        }
    }

    private static void ValidateFailure(IngestionCatalogDeliveryFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.DeliveryId == Guid.Empty ||
            failure.BatchId == Guid.Empty ||
            failure.LeaseToken == Guid.Empty ||
            string.IsNullOrWhiteSpace(failure.ItemKey) || failure.ItemKey.Length > 200 ||
            string.IsNullOrWhiteSpace(failure.FailureCode) || failure.FailureCode.Length > 200 ||
            string.IsNullOrWhiteSpace(failure.FailureDetail) || failure.FailureDetail.Length > 4_000)
        {
            throw Failure(
                "INGESTION_DELIVERY_FAILURE_INVALID",
                500,
                "The Catalog delivery failure contract is invalid.",
                "Correct the Ingestion delivery failure classifier before retrying.");
        }
    }

    private static void ValidateLeaseRequest(
        string workerIdentity,
        int limit,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leaseExpiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(workerIdentity) ||
            workerIdentity.Length > 200 ||
            workerIdentity.Any(char.IsControl))
        {
            throw Failure(
                "INGESTION_DELIVERY_WORKER_INVALID",
                500,
                "A bounded Catalog delivery worker identity is required.",
                "Correct the Ingestion worker configuration.");
        }

        if (limit is < 1 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        RequireUtc(leasedAtUtc, nameof(leasedAtUtc));
        RequireUtc(leaseExpiresAtUtc, nameof(leaseExpiresAtUtc));
        if (leaseExpiresAtUtc <= leasedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseExpiresAtUtc));
        }
    }

    private static void EnsureIdentity(
        DeliveryState current,
        Guid batchId,
        string itemKey,
        CatalogIngestionCommandOutcome? outcome)
    {
        if (batchId == Guid.Empty ||
            string.IsNullOrWhiteSpace(itemKey) ||
            current.BatchId != batchId ||
            !string.Equals(current.ItemKey, itemKey, StringComparison.Ordinal) ||
            outcome is not null &&
            (outcome.CommandId != current.DeliveryId ||
             outcome.IngestionBatchId != current.BatchId ||
             !string.Equals(outcome.IngestionItemKey, current.ItemKey, StringComparison.Ordinal)))
        {
            throw Failure(
                "INGESTION_DELIVERY_OUTCOME_IDENTITY_MISMATCH",
                409,
                "The Catalog result identifies a different Ingestion delivery.",
                "Replay the exact owner-produced result for this delivery identity.");
        }
    }

    private static void EnsureActiveLease(
        DeliveryState current,
        Guid leaseToken,
        DateTimeOffset changedAtUtc)
    {
        if (leaseToken == Guid.Empty ||
            current.State != 2 ||
            current.LeaseToken != leaseToken ||
            current.LeaseExpiresAtUtc is null ||
            current.LeaseExpiresAtUtc <= changedAtUtc)
        {
            throw new IngestionCatalogDeliveryLeaseLostException(current.DeliveryId);
        }
    }

    private static void EnsureTerminalOutcomeMatches(
        DeliveryState current,
        int targetState,
        CatalogIngestionCommandOutcome outcome)
    {
        if (current.State != targetState ||
            current.CatalogListingId != outcome.ListingId ||
            current.CatalogListingRevisionId != outcome.ListingRevisionId ||
            !string.Equals(current.FailureCode, outcome.FailureCode, StringComparison.Ordinal) ||
            !string.Equals(current.FailureDetail, outcome.FailureDetail, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_DELIVERY_OUTCOME_CONFLICT",
                409,
                "The delivery already has a different terminal Catalog outcome.",
                "Use the exact original Catalog outcome.");
        }
    }

    private sealed record DeliveryCandidate(
        Guid DeliveryId,
        Guid BatchId,
        string ItemKey,
        string CommandType,
        byte[] CommandDocument,
        string CommandDigest,
        int AttemptCount);

    private sealed record DeliveryState(
        Guid DeliveryId,
        Guid BatchId,
        string ItemKey,
        int State,
        Guid? LeaseToken,
        DateTimeOffset? LeaseExpiresAtUtc,
        Guid? CatalogListingId,
        Guid? CatalogListingRevisionId,
        string? FailureCode,
        string? FailureDetail);
}
