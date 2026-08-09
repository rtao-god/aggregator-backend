using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Aggregator.Ingestion.Infrastructure;

public sealed partial class PostgresIngestionProducerRegistrationStore
{
    private static IngestionProducerRegistrationSnapshot RestoreReplay(
        StoredProducerCommand stored,
        IngestionProducerRegistrationMutation mutation)
    {
        if (!string.Equals(
                stored.RequestDigest,
                mutation.CommandIdentity.RequestDigest,
                StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_PRODUCER_IDEMPOTENCY_CONFLICT",
                409,
                $"Idempotency key '{mutation.CommandIdentity.Key}' is already bound to another producer-registration command.",
                "Use the original request or a new Idempotency-Key.",
                mutation.Registration.ProducerIdentity,
                mutation.ExpectedAggregateRevision);
        }

        if (!string.Equals(stored.ProducerIdentity, mutation.Registration.ProducerIdentity, StringComparison.Ordinal) ||
            !string.Equals(stored.CallerServiceIdentity, mutation.CallerServiceIdentity, StringComparison.Ordinal) ||
            !string.Equals(
                IngestionCanonicalJson.ComputeDigest(stored.ResultDocument),
                stored.ResultDigest,
                StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_PRODUCER_COMMAND_CORRUPT",
                500,
                "Stored producer-registration command metadata or result digest is inconsistent.",
                "Stop producer-registration writes and restore the Ingestion command ledger from verified state.",
                mutation.Registration.ProducerIdentity,
                mutation.ExpectedAggregateRevision);
        }

        var snapshot = IngestionCanonicalJson.Deserialize<IngestionProducerRegistrationSnapshot>(
            stored.ResultDocument);
        EnsureSnapshot(snapshot);
        if (!string.Equals(snapshot.ProducerIdentity, mutation.Registration.ProducerIdentity, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_PRODUCER_COMMAND_RESULT_CORRUPT",
                500,
                "Stored producer-registration result belongs to another producer identity.",
                "Restore the Ingestion command ledger from verified state.",
                mutation.Registration.ProducerIdentity,
                mutation.ExpectedAggregateRevision);
        }

        return snapshot;
    }

    private static void EnsureExpectedRevision(
        IngestionProducerRegistrationSnapshot? current,
        IngestionProducerRegistrationMutation mutation)
    {
        var expected = mutation.ExpectedAggregateRevision;
        if (current is null)
        {
            if (expected == 0 && mutation.Registration.AggregateRevision == 1)
            {
                return;
            }

            throw RevisionConflict(mutation.Registration.ProducerIdentity, expected, null);
        }

        if (current.AggregateRevision != expected ||
            mutation.Registration.AggregateRevision != checked(expected + 1))
        {
            throw RevisionConflict(
                mutation.Registration.ProducerIdentity,
                expected,
                current.AggregateRevision);
        }
    }

    private static void ValidateMutation(IngestionProducerRegistrationMutation mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        ArgumentNullException.ThrowIfNull(mutation.Registration);
        ArgumentNullException.ThrowIfNull(mutation.CommandIdentity);
        if (!string.Equals(
                mutation.CallerServiceIdentity,
                mutation.Registration.UpdatedByServiceIdentity,
                StringComparison.Ordinal) ||
            mutation.ExpectedAggregateRevision < 0)
        {
            throw Failure(
                "INGESTION_PRODUCER_MUTATION_INVALID",
                500,
                "Producer-registration mutation metadata is inconsistent.",
                "Correct the Ingestion producer-registration application owner.",
                mutation.Registration.ProducerIdentity,
                mutation.ExpectedAggregateRevision);
        }

        EnsureSnapshot(mutation.Registration);
    }

    private static void EnsureSnapshot(IngestionProducerRegistrationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SupportedContractRevisions is null)
        {
            throw Corrupt(snapshot, "Producer-registration state has no contract revision set.");
        }

        var revisions = snapshot.SupportedContractRevisions.ToArray();
        if (string.IsNullOrWhiteSpace(snapshot.ProducerIdentity) ||
            snapshot.ProducerIdentity.Length > 200 ||
            snapshot.AggregateRevision <= 0 ||
            revisions.Length == 0 ||
            !revisions.SequenceEqual(revisions.Order()) ||
            revisions.Distinct().Count() != revisions.Length ||
            revisions.Any(revision => revision != AggregatorCandidateIngestionContract.Revision) ||
            string.IsNullOrWhiteSpace(snapshot.UpdatedByServiceIdentity) ||
            snapshot.UpdatedByServiceIdentity.Length > 200 ||
            string.IsNullOrWhiteSpace(snapshot.Reason) ||
            snapshot.Reason.Length is < 8 or > 1_000 ||
            snapshot.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            !string.Equals(
                snapshot.ContentDigest,
                IngestionProducerRegistrationService.ComputeContentDigest(
                    snapshot.ProducerIdentity,
                    snapshot.Active,
                    snapshot.SupportedContractRevisions,
                    snapshot.AggregateRevision),
                StringComparison.Ordinal))
        {
            throw Corrupt(snapshot, "Producer-registration state violates its canonical owner contract.");
        }
    }

    private static IngestionProducerRegistrationSnapshot RestoreSnapshot(
        string producerIdentity,
        bool active,
        int[] supportedContractRevisions,
        long aggregateRevision,
        string contentDigest,
        string updatedByServiceIdentity,
        string reason,
        DateTimeOffset updatedAtUtc)
    {
        var snapshot = new IngestionProducerRegistrationSnapshot(
            producerIdentity,
            active,
            Array.AsReadOnly(supportedContractRevisions),
            aggregateRevision,
            contentDigest,
            updatedByServiceIdentity,
            reason,
            updatedAtUtc);
        EnsureSnapshot(snapshot);
        return snapshot;
    }

    private static IngestionApplicationException Corrupt(
        IngestionProducerRegistrationSnapshot snapshot,
        string detail) =>
        Failure(
            "INGESTION_PRODUCER_REGISTRATION_CORRUPT",
            500,
            detail,
            "Stop producer authorization and restore the registry from immutable revision history.",
            snapshot.ProducerIdentity,
            snapshot.AggregateRevision);

    private static IngestionApplicationException RevisionConflict(
        string producerIdentity,
        long expectedAggregateRevision,
        long? actualAggregateRevision) =>
        Failure(
            "INGESTION_PRODUCER_REVISION_CONFLICT",
            409,
            $"Producer registration expected revision '{expectedAggregateRevision}' but current revision is '{actualAggregateRevision?.ToString() ?? "absent"}'.",
            "Read the current producer registration and resubmit against its exact aggregate revision.",
            producerIdentity,
            expectedAggregateRevision,
            actualAggregateRevision);

    private static IngestionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        string? producerIdentity = null,
        long? expectedAggregateRevision = null,
        long? actualAggregateRevision = null) =>
        new(
            "Ingestion.ProducerRegistry",
            code,
            statusCode,
            detail,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["producerIdentity"] = producerIdentity,
                ["expectedAggregateRevision"] = expectedAggregateRevision,
                ["actualAggregateRevision"] = actualAggregateRevision,
            });

    private sealed record StoredProducerCommand(
        string RequestDigest,
        string ProducerIdentity,
        byte[] ResultDocument,
        string ResultDigest,
        string CallerServiceIdentity);
}
