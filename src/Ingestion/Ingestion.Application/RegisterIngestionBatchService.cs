using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public sealed record RegisterIngestionBatchCommand(
    AggregatorCandidateIngestionManifest Manifest,
    string ExpectedManifestDigest,
    string IdempotencyKey,
    string CallerServiceIdentity);

public sealed class RegisterIngestionBatchService
{
    private readonly IIngestionProducerRegistry _producerRegistry;
    private readonly ICatalogIngestionReferenceReader _catalogReferenceReader;
    private readonly IIngestionBatchRepository _repository;
    private readonly IIngestionClock _clock;
    private readonly IIngestionIdSource _idSource;

    public RegisterIngestionBatchService(
        IIngestionProducerRegistry producerRegistry,
        ICatalogIngestionReferenceReader catalogReferenceReader,
        IIngestionBatchRepository repository,
        IIngestionClock clock,
        IIngestionIdSource idSource)
    {
        _producerRegistry = producerRegistry ?? throw new ArgumentNullException(nameof(producerRegistry));
        _catalogReferenceReader = catalogReferenceReader ??
            throw new ArgumentNullException(nameof(catalogReferenceReader));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idSource = idSource ?? throw new ArgumentNullException(nameof(idSource));
    }

    public async Task<IngestionBatchRegistrationResult> RegisterAsync(
        RegisterIngestionBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Manifest);
        if (string.IsNullOrWhiteSpace(command.CallerServiceIdentity) ||
            command.CallerServiceIdentity.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Commands",
                "INGESTION_CALLER_IDENTITY_INVALID",
                401,
                "The caller service identity is missing or invalid.",
                "Authenticate with the dedicated collector service identity.");
        }

        var validatedManifest = IngestionPackageValidator.ValidateManifest(
            command.Manifest,
            command.ExpectedManifestDigest);
        var producer = await _producerRegistry.GetAsync(
            command.Manifest.ProducerIdentity,
            cancellationToken);
        if (producer is null || !producer.Active)
        {
            throw new IngestionApplicationException(
                "Ingestion.ProducerRegistry",
                "INGESTION_PRODUCER_NOT_ALLOWED",
                403,
                $"Producer '{command.Manifest.ProducerIdentity}' is not registered as active.",
                "Register and authorize the exact collector producer build before retrying.");
        }

        if (producer.SupportedContractRevisions is null ||
            !producer.SupportedContractRevisions.Contains(command.Manifest.ContractRevision))
        {
            throw new IngestionApplicationException(
                "Ingestion.ProducerRegistry",
                "INGESTION_PRODUCER_CONTRACT_UNSUPPORTED",
                422,
                $"Producer '{producer.Identity}' is not authorized for ingestion contract revision '{command.Manifest.ContractRevision}'.",
                "Update the producer registration after compatibility proof.");
        }

        var catalogReference = await _catalogReferenceReader.GetAsync(
            command.Manifest.TargetSiteKey,
            command.Manifest.TargetCatalogKey,
            cancellationToken);
        if (catalogReference is null)
        {
            throw new IngestionApplicationException(
                "Ingestion.CatalogProjection",
                "INGESTION_TARGET_CATALOG_UNKNOWN",
                422,
                $"Target catalog '{command.Manifest.TargetCatalogKey}' is unknown to the local Catalog projection.",
                "Apply the missing Catalog reference event or rebuild the Ingestion Catalog projection.");
        }

        if (!string.Equals(
                catalogReference.SiteKey,
                command.Manifest.TargetSiteKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                catalogReference.CatalogKey,
                command.Manifest.TargetCatalogKey,
                StringComparison.Ordinal))
        {
            throw new IngestionApplicationException(
                "Ingestion.CatalogProjection",
                "INGESTION_TARGET_CATALOG_IDENTITY_MISMATCH",
                422,
                "The local Catalog projection does not match the requested site and catalog identity.",
                "Repair or rebuild the exact Catalog reference projection before accepting packages.");
        }

        if (catalogReference.ActiveConfigurationRevisionId !=
            command.Manifest.TargetCatalogConfigurationRevisionId)
        {
            throw new IngestionApplicationException(
                "Ingestion.CatalogProjection",
                "INGESTION_TARGET_CONFIGURATION_REVISION_MISMATCH",
                409,
                "The package targets a Catalog configuration revision that is not active in the local projection.",
                "Regenerate the collector adapter package against the exact active Catalog configuration revision.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["targetCatalogKey"] = command.Manifest.TargetCatalogKey,
                    ["packageConfigurationRevisionId"] =
                        command.Manifest.TargetCatalogConfigurationRevisionId,
                    ["activeConfigurationRevisionId"] =
                        catalogReference.ActiveConfigurationRevisionId,
                    ["catalogAggregateRevision"] = catalogReference.AggregateRevision,
                });
        }

        if (catalogReference.SupportedListingKinds is null ||
            catalogReference.SupportedListingKinds.Count == 0 ||
            catalogReference.SupportedListingKinds.Any(kind =>
                kind is not IngestionEntityKindContract.Place and not IngestionEntityKindContract.Provider))
        {
            throw new IngestionApplicationException(
                "Ingestion.CatalogProjection",
                "INGESTION_TARGET_LISTING_KINDS_INVALID",
                503,
                "The local Catalog projection does not expose a valid public listing-kind contract.",
                "Rebuild the Catalog reference projection from the producer-owned Catalog event.");
        }

        var registeredAtUtc = _clock.GetUtcNow();
        if (registeredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new IngestionApplicationException(
                "Ingestion.Clock",
                "INGESTION_CLOCK_NOT_UTC",
                500,
                "The Ingestion clock returned a non-UTC timestamp.",
                "Correct the composition root to supply a UTC clock.");
        }

        var batch = ImportBatch.Create(
            ImportBatchId.Create(_idSource.CreateId()),
            command.Manifest.ProducerIdentity,
            command.Manifest.ProducerBuild,
            command.Manifest.CollectorExportId,
            command.Manifest.CollectorExportDigest,
            command.Manifest.TargetSiteKey,
            command.Manifest.TargetCatalogKey,
            command.Manifest.TargetCatalogConfigurationRevisionId,
            command.Manifest.ItemCount,
            validatedManifest.ManifestDigest,
            command.Manifest.ItemIndexDigest,
            command.Manifest.PayloadDigest,
            validatedManifest.PayloadArtifact.ObjectKey,
            validatedManifest.PayloadArtifact.ContentDigest,
            validatedManifest.PayloadArtifact.Size,
            validatedManifest.PayloadArtifact.ContentType,
            registeredAtUtc);
        var requestDigest = IngestionCanonicalJson.ComputeDigest(new
        {
            validatedManifest.ManifestDigest,
            command.CallerServiceIdentity,
            command.Manifest.TargetCatalogKey,
            command.Manifest.TargetCatalogConfigurationRevisionId,
        });
        var commandIdentity = IngestionCommandIdentity.Create(
            $"ingestion.batch.register:{command.Manifest.TargetCatalogKey}",
            command.IdempotencyKey,
            requestDigest);
        return await _repository.RegisterAsync(
            batch,
            command.Manifest,
            commandIdentity,
            command.CallerServiceIdentity,
            cancellationToken);
    }
}
