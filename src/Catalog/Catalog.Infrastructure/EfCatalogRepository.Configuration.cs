using System.Text.Json;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository
{
    public Task AddConfigurationAsync(
        ProductConfiguration configuration,
        byte[] canonicalDocument,
        Guid importedByActorId,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(canonicalDocument);
        if (canonicalDocument.Length == 0)
        {
            throw new ArgumentException("Canonical configuration document cannot be empty.", nameof(canonicalDocument));
        }

        if (importedByActorId == Guid.Empty)
        {
            throw new ArgumentException("Configuration import actor ID is required.", nameof(importedByActorId));
        }

        var validationProof = CatalogProductConfigurationValidation.Create(
            configuration,
            canonicalDocument);
        return ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            var duplicate = await _dbContext.ConfigurationRevisions
                .AsNoTracking()
                .AnyAsync(
                    row => row.Id == configuration.RevisionId ||
                           (row.CatalogKey == configuration.Catalog.Key.Value && row.ContentDigest == configuration.Digest),
                    innerCancellationToken);
            if (duplicate)
            {
                throw new CatalogConflictException(
                    $"Configuration revision '{configuration.RevisionId}' or digest '{configuration.Digest}' already exists.");
            }

            _ = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO catalog.configuration_revision
                (
                    id,
                    site_key,
                    catalog_key,
                    content_digest,
                    canonical_document,
                    created_at_utc,
                    imported_at_utc,
                    validation_contract_identity,
                    validation_contract_revision,
                    validation_state,
                    validation_result_digest,
                    validated_at_utc
                )
                VALUES
                (
                    {configuration.RevisionId},
                    {configuration.Site.Key.Value},
                    {configuration.Catalog.Key.Value},
                    {configuration.Digest},
                    {canonicalDocument},
                    {configuration.CreatedAtUtc},
                    {importedAtUtc},
                    {validationProof.ContractIdentity},
                    {validationProof.ContractRevision},
                    {(int)validationProof.State},
                    {validationProof.ResultDigest},
                    {importedAtUtc}
                );
                """,
                innerCancellationToken);
            _ = await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO catalog.configuration_import_actor
                (
                    configuration_revision_id,
                    imported_by_actor_id
                )
                VALUES
                (
                    {configuration.RevisionId},
                    {importedByActorId}
                );
                """,
                innerCancellationToken);
        }, cancellationToken);
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
        CatalogConfigurationActivationOutboxFactory outboxFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        ArgumentNullException.ThrowIfNull(outboxFactory);
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

            Guid? previousConfigurationRevisionId;
            long aggregateRevision;
            // Guid.Empty is the internal persistence encoding of the explicit Absent expectation.
            if (expectedConfigurationRevisionId == Guid.Empty)
            {
                if (current is not null)
                {
                    throw new CatalogConflictException(
                        $"Catalog '{catalogKey}' expected no active configuration but is at '{current.ConfigurationRevisionId}'.");
                }

                previousConfigurationRevisionId = null;
                aggregateRevision = 1;
                current = new ActiveCatalogConfigurationRow
                {
                    CatalogKey = catalogKey.Value,
                    ConfigurationRevisionId = configurationRevisionId,
                    ActivatedByActorId = actorId,
                    AggregateRevision = aggregateRevision,
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

                previousConfigurationRevisionId = current.ConfigurationRevisionId;
                aggregateRevision = checked(current.AggregateRevision + 1);
                current.ConfigurationRevisionId = configurationRevisionId;
                current.ActivatedByActorId = actorId;
                current.AggregateRevision = aggregateRevision;
                current.ActivatedAtUtc = activatedAtUtc;
            }

            AddOutbox(outboxFactory(previousConfigurationRevisionId, aggregateRevision));
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
