using System.Text.Json;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository
{
    public async Task AddConfigurationAsync(
        ProductConfiguration configuration,
        byte[] canonicalDocument,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(canonicalDocument);
        if (canonicalDocument.Length == 0)
        {
            throw new ArgumentException("Canonical configuration document cannot be empty.", nameof(canonicalDocument));
        }

        var duplicate = await _dbContext.ConfigurationRevisions
            .AsNoTracking()
            .AnyAsync(
                row => row.Id == configuration.RevisionId ||
                       (row.CatalogKey == configuration.Catalog.Key.Value && row.ContentDigest == configuration.Digest),
                cancellationToken);
        if (duplicate)
        {
            throw new CatalogConflictException(
                $"Configuration revision '{configuration.RevisionId}' or digest '{configuration.Digest}' already exists.");
        }

        _dbContext.ConfigurationRevisions.Add(new CatalogConfigurationRevisionRow
        {
            Id = configuration.RevisionId,
            SiteKey = configuration.Site.Key.Value,
            CatalogKey = configuration.Catalog.Key.Value,
            ContentDigest = configuration.Digest,
            CanonicalDocument = canonicalDocument.ToArray(),
            CreatedAtUtc = configuration.CreatedAtUtc,
            ImportedAtUtc = importedAtUtc,
        });
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ProductConfiguration?> GetConfigurationAsync(
        Guid configurationRevisionId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.ConfigurationRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == configurationRevisionId, cancellationToken);
        return row is null ? null : RehydrateConfiguration(row);
    }

    public async Task<ProductConfiguration?> GetActiveConfigurationAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        var row = await (
                from active in _dbContext.ActiveConfigurations.AsNoTracking()
                join configuration in _dbContext.ConfigurationRevisions.AsNoTracking()
                    on active.ConfigurationRevisionId equals configuration.Id
                where active.CatalogKey == catalogKey.Value
                select configuration)
            .SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : RehydrateConfiguration(row);
    }

    public async Task ActivateConfigurationAsync(
        CatalogKey catalogKey,
        Guid configurationRevisionId,
        Guid expectedConfigurationRevisionId,
        Guid actorId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        await ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            var targetExists = await _dbContext.ConfigurationRevisions
                .AsNoTracking()
                .AnyAsync(
                    row => row.Id == configurationRevisionId && row.CatalogKey == catalogKey.Value,
                    innerCancellationToken);
            if (!targetExists)
            {
                throw new CatalogNotFoundException("product-configuration-revision", configurationRevisionId);
            }

            var current = await _dbContext.ActiveConfigurations
                .SingleOrDefaultAsync(row => row.CatalogKey == catalogKey.Value, innerCancellationToken);

            // Guid.Empty is the internal persistence encoding of the explicit Absent expectation.
            if (expectedConfigurationRevisionId == Guid.Empty)
            {
                if (current is not null)
                {
                    throw new CatalogConflictException(
                        $"Catalog '{catalogKey}' expected no active configuration but is at '{current.ConfigurationRevisionId}'.");
                }

                current = new ActiveCatalogConfigurationRow
                {
                    CatalogKey = catalogKey.Value,
                    ConfigurationRevisionId = configurationRevisionId,
                    ActivatedByActorId = actorId,
                    ActivatedAtUtc = activatedAtUtc,
                };
                _dbContext.ActiveConfigurations.Add(current);
            }
            else
            {
                if (current is null || current.ConfigurationRevisionId != expectedConfigurationRevisionId)
                {
                    throw new CatalogConflictException(
                        $"Catalog '{catalogKey}' expected active configuration '{expectedConfigurationRevisionId}' but is at '{current?.ConfigurationRevisionId.ToString() ?? "absent"}'.");
                }

                current.ConfigurationRevisionId = configurationRevisionId;
                current.ActivatedByActorId = actorId;
                current.ActivatedAtUtc = activatedAtUtc;
            }

            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);
    }

    private static ProductConfiguration RehydrateConfiguration(CatalogConfigurationRevisionRow row)
    {
        var contract = JsonSerializer.Deserialize<ProductConfigurationContract>(
                row.CanonicalDocument,
                CatalogPersistenceJson.Options)
            ?? throw new InvalidOperationException(
                $"Configuration revision '{row.Id}' contains an empty canonical document.");
        if (contract.RevisionId != row.Id)
        {
            throw new InvalidOperationException(
                $"Configuration revision row '{row.Id}' contains document '{contract.RevisionId}'.");
        }

        return CatalogPersistenceRehydration.Configuration(contract, row.ContentDigest);
    }
}
