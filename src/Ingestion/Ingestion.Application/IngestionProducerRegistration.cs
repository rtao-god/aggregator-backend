using Aggregator.Ingestion.Contracts;

namespace Aggregator.Ingestion.Application;

/// <summary>Current Ingestion-owned producer authorization revision.</summary>
public sealed record IngestionProducerRegistrationSnapshot(
    string ProducerIdentity,
    bool Active,
    IReadOnlyList<int> SupportedContractRevisions,
    long AggregateRevision,
    string ContentDigest,
    string UpdatedByServiceIdentity,
    string Reason,
    DateTimeOffset UpdatedAtUtc);

public sealed record PutIngestionProducerRegistrationCommand(
    string ProducerIdentity,
    long ExpectedAggregateRevision,
    bool Active,
    IReadOnlyList<int> SupportedContractRevisions,
    string Reason,
    string IdempotencyKey,
    string CallerServiceIdentity);

public sealed record IngestionProducerRegistrationMutation(
    IngestionProducerRegistrationSnapshot Registration,
    long ExpectedAggregateRevision,
    IngestionCommandIdentity CommandIdentity,
    string CallerServiceIdentity);

public sealed record IngestionProducerRegistrationMutationResult(
    IngestionProducerRegistrationSnapshot Registration,
    bool Replayed);

/// <summary>Persists the current producer registration, immutable history, and command result atomically.</summary>
public interface IIngestionProducerRegistrationStore
{
    public Task<IngestionProducerRegistrationMutationResult> PutAsync(
        IngestionProducerRegistrationMutation mutation,
        CancellationToken cancellationToken);

    public Task<IngestionProducerRegistrationSnapshot?> ReadAsync(
        string producerIdentity,
        CancellationToken cancellationToken);
}

/// <summary>Owns producer registration validation, revision identity, and canonical command hashing.</summary>
public sealed class IngestionProducerRegistrationService(
    IIngestionProducerRegistrationStore store,
    IIngestionClock clock)
{
    private static readonly int[] BackendSupportedContractRevisions =
        [AggregatorCandidateIngestionContract.Revision];

    public Task<IngestionProducerRegistrationMutationResult> PutAsync(
        PutIngestionProducerRegistrationCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var producerIdentity = RequireIdentity(
            command.ProducerIdentity,
            "producer identity",
            400);
        var callerIdentity = RequireIdentity(
            command.CallerServiceIdentity,
            "caller service identity",
            403);
        if (command.ExpectedAggregateRevision < 0)
        {
            throw Failure(
                "INGESTION_PRODUCER_EXPECTED_REVISION_INVALID",
                400,
                "Expected producer registration revision cannot be negative.",
                "Use zero for an absent registration or the exact current aggregate revision.",
                producerIdentity,
                command.ExpectedAggregateRevision);
        }

        var supportedRevisions = ValidateSupportedRevisions(
            command.SupportedContractRevisions,
            producerIdentity);
        var reason = RequireReason(command.Reason, producerIdentity);
        var updatedAtUtc = RequireUtc(clock.GetUtcNow());
        var aggregateRevision = checked(command.ExpectedAggregateRevision + 1);
        var contentDocument = new ProducerRegistrationContentDocument(
            producerIdentity,
            command.Active,
            supportedRevisions,
            aggregateRevision);
        var contentDigest = IngestionCanonicalJson.ComputeDigest(contentDocument);
        var requestDocument = new ProducerRegistrationRequestDocument(
            producerIdentity,
            command.ExpectedAggregateRevision,
            command.Active,
            supportedRevisions,
            reason,
            callerIdentity);
        var commandIdentity = IngestionCommandIdentity.Create(
            "producer-registration",
            command.IdempotencyKey,
            IngestionCanonicalJson.ComputeDigest(requestDocument));
        var snapshot = new IngestionProducerRegistrationSnapshot(
            producerIdentity,
            command.Active,
            supportedRevisions,
            aggregateRevision,
            contentDigest,
            callerIdentity,
            reason,
            updatedAtUtc);
        return store.PutAsync(
            new IngestionProducerRegistrationMutation(
                snapshot,
                command.ExpectedAggregateRevision,
                commandIdentity,
                callerIdentity),
            cancellationToken);
    }

    public Task<IngestionProducerRegistrationSnapshot?> ReadAsync(
        string producerIdentity,
        CancellationToken cancellationToken) =>
        store.ReadAsync(
            RequireIdentity(producerIdentity, "producer identity", 400),
            cancellationToken);

    public static IngestionProducerRegistrationDto ToDto(
        IngestionProducerRegistrationSnapshot registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return new IngestionProducerRegistrationDto(
            registration.ProducerIdentity,
            registration.Active,
            registration.SupportedContractRevisions,
            registration.AggregateRevision,
            registration.ContentDigest,
            registration.UpdatedByServiceIdentity,
            registration.Reason,
            registration.UpdatedAtUtc);
    }

    private static IReadOnlyList<int> ValidateSupportedRevisions(
        IReadOnlyList<int> revisions,
        string producerIdentity)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if (revisions.Count == 0 || revisions.Count > 32)
        {
            throw Failure(
                "INGESTION_PRODUCER_REVISIONS_INVALID",
                400,
                "Producer registration must contain between one and 32 contract revisions.",
                "Declare the exact backend-supported candidate-ingestion revisions.",
                producerIdentity);
        }

        var normalized = revisions.Order().ToArray();
        if (normalized.Distinct().Count() != normalized.Length ||
            normalized.Any(revision => !BackendSupportedContractRevisions.Contains(revision)))
        {
            throw Failure(
                "INGESTION_PRODUCER_REVISION_UNSUPPORTED",
                422,
                $"Producer registration contains a duplicate or unsupported contract revision. Backend supports: {string.Join(", ", BackendSupportedContractRevisions)}.",
                "Register only exact contract revisions published by this backend.",
                producerIdentity);
        }

        return Array.AsReadOnly(normalized);
    }

    private static string RequireIdentity(
        string value,
        string meaning,
        int statusCode)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 200 ||
            value.Any(char.IsControl))
        {
            throw Failure(
                "INGESTION_PRODUCER_IDENTITY_INVALID",
                statusCode,
                $"The {meaning} must be non-empty, control-free, and at most 200 characters.",
                "Use the exact OIDC workload subject identity.");
        }

        return value.Trim();
    }

    private static string RequireReason(string value, string producerIdentity)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length is < 8 or > 1_000 ||
            value.Any(char.IsControl))
        {
            throw Failure(
                "INGESTION_PRODUCER_REASON_INVALID",
                400,
                "Producer registration reason must contain 8 to 1000 control-free characters.",
                "Provide an auditable reason for the registration change.",
                producerIdentity);
        }

        return value.Trim();
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "INGESTION_PRODUCER_CLOCK_NOT_UTC",
                500,
                "The Ingestion producer-registration clock returned a non-UTC timestamp.",
                "Correct the Ingestion clock owner before changing producer registrations.");
        }

        return value;
    }

    private static IngestionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        string? producerIdentity = null,
        long? expectedAggregateRevision = null) =>
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
            });

    private sealed record ProducerRegistrationContentDocument(
        string ProducerIdentity,
        bool Active,
        IReadOnlyList<int> SupportedContractRevisions,
        long AggregateRevision);

    private sealed record ProducerRegistrationRequestDocument(
        string ProducerIdentity,
        long ExpectedAggregateRevision,
        bool Active,
        IReadOnlyList<int> SupportedContractRevisions,
        string Reason,
        string CallerServiceIdentity);
}
