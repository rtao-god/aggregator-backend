using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Reads only the Ingestion-owned producer authorization registry.</summary>
public sealed class EfIngestionProducerRegistry : IIngestionProducerRegistry
{
    private readonly IngestionDbContext _dbContext;

    public EfIngestionProducerRegistry(IngestionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RegisteredIngestionProducer?> GetAsync(
        string producerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerIdentity);
        var row = await _dbContext.Producers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Identity == producerIdentity, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (row.SupportedContractRevisions is null ||
            row.SupportedContractRevisions.Length == 0 ||
            row.SupportedContractRevisions.Any(revision => revision <= 0) ||
            row.SupportedContractRevisions.Distinct().Count() != row.SupportedContractRevisions.Length)
        {
            throw new IngestionApplicationException(
                "Ingestion.ProducerRegistry",
                "INGESTION_PRODUCER_REGISTRY_CORRUPT",
                500,
                $"Producer '{row.Identity}' has an invalid supported-contract revision set.",
                "Repair the producer registration through an Ingestion owner command.");
        }

        return new RegisteredIngestionProducer(
            row.Identity,
            row.Active,
            Array.AsReadOnly(
                row.SupportedContractRevisions
                    .Order()
                    .ToArray()));
    }
}

/// <summary>Reads the Ingestion-local projection of producer-owned Catalog identity events.</summary>
public sealed class EfCatalogIngestionReferenceReader : ICatalogIngestionReferenceReader
{
    private readonly IngestionDbContext _dbContext;

    public EfCatalogIngestionReferenceReader(IngestionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<CatalogIngestionReference?> GetAsync(
        string siteKey,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        var row = await _dbContext.CatalogReferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.SiteKey == siteKey && candidate.CatalogKey == catalogKey,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (row.ActiveConfigurationRevisionId == Guid.Empty || row.AggregateRevision <= 0)
        {
            throw ProjectionCorrupt(row, "The active configuration identity and aggregate revision must be present.");
        }

        if (row.SupportedListingKinds is null || row.SupportedListingKinds.Length == 0)
        {
            throw ProjectionCorrupt(row, "At least one supported public listing kind is required.");
        }

        var listingKinds = new List<IngestionEntityKindContract>(row.SupportedListingKinds.Length);
        var uniqueKinds = new HashSet<IngestionEntityKindContract>();
        foreach (var rawKind in row.SupportedListingKinds.Order())
        {
            if (!Enum.IsDefined(typeof(IngestionEntityKindContract), rawKind))
            {
                throw ProjectionCorrupt(row, $"Listing kind value '{rawKind}' is unsupported.");
            }

            var kind = (IngestionEntityKindContract)rawKind;
            if (kind is not IngestionEntityKindContract.Place and not IngestionEntityKindContract.Provider)
            {
                throw ProjectionCorrupt(row, $"Entity kind '{kind}' cannot be a public listing kind.");
            }

            if (!uniqueKinds.Add(kind))
            {
                throw ProjectionCorrupt(row, $"Listing kind '{kind}' is duplicated.");
            }

            listingKinds.Add(kind);
        }

        return new CatalogIngestionReference(
            row.SiteKey,
            row.CatalogKey,
            row.ActiveConfigurationRevisionId,
            listingKinds.AsReadOnly(),
            row.AggregateRevision);
    }

    private static IngestionApplicationException ProjectionCorrupt(
        CatalogIngestionReferenceRow row,
        string detail) =>
        new(
            "Ingestion.CatalogProjection",
            "INGESTION_CATALOG_PROJECTION_CORRUPT",
            503,
            $"Catalog projection '{row.SiteKey}/{row.CatalogKey}' is invalid. {detail}",
            "Replay the producer-owned Catalog reference event or rebuild the Ingestion Catalog projection.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["siteKey"] = row.SiteKey,
                ["catalogKey"] = row.CatalogKey,
                ["aggregateRevision"] = row.AggregateRevision,
                ["activeConfigurationRevisionId"] = row.ActiveConfigurationRevisionId,
            });
}
