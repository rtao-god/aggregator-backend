using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public interface IIngestionClock
{
    public DateTimeOffset GetUtcNow();
}

public interface IIngestionIdSource
{
    public Guid CreateId();
}

public sealed record RegisteredIngestionProducer(
    string Identity,
    bool Active,
    IReadOnlyList<int> SupportedContractRevisions);

public interface IIngestionProducerRegistry
{
    public Task<RegisteredIngestionProducer?> GetAsync(
        string producerIdentity,
        CancellationToken cancellationToken);
}

public sealed record CatalogIngestionReference(
    string SiteKey,
    string CatalogKey,
    Guid ActiveConfigurationRevisionId,
    IReadOnlyList<IngestionEntityKindContract> SupportedListingKinds,
    long AggregateRevision);

public interface ICatalogIngestionReferenceReader
{
    public Task<CatalogIngestionReference?> GetAsync(
        string siteKey,
        string catalogKey,
        CancellationToken cancellationToken);
}

public sealed record IngestionCommandIdentity(string Scope, string Key, string RequestDigest)
{
    public static IngestionCommandIdentity Create(string scope, string key, string requestDigest)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 150)
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_IDEMPOTENCY_SCOPE_INVALID",
                500,
                "The command owner supplied an invalid idempotency scope.",
                "Correct the Ingestion composition root before retrying.");
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl))
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_IDEMPOTENCY_KEY_INVALID",
                400,
                "A non-empty Idempotency-Key of at most 200 characters is required.",
                "Submit the command with one stable Idempotency-Key.");
        }

        if (requestDigest is not { Length: 64 } ||
            requestDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_REQUEST_DIGEST_INVALID",
                500,
                "The command request digest is invalid.",
                "Correct canonical request hashing before retrying.");
        }

        return new IngestionCommandIdentity(scope, key, requestDigest);
    }
}

public sealed record IngestionBatchRegistrationResult(ImportBatch Batch, bool Replayed);

public interface IIngestionBatchRepository
{
    public Task<IngestionBatchRegistrationResult> RegisterAsync(
        ImportBatch batch,
        AggregatorCandidateIngestionManifest manifest,
        IngestionCommandIdentity commandIdentity,
        string callerServiceIdentity,
        CancellationToken cancellationToken);

    public Task<ImportBatch?> GetAsync(
        ImportBatchId batchId,
        CancellationToken cancellationToken);
}
