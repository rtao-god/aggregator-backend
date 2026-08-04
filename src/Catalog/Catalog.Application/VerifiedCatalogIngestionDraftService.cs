using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public interface ICatalogIngestionTargetProjectionWriter
{
    public Task UpsertAsync(
        string siteKey,
        string catalogKey,
        Guid activeConfigurationRevisionId,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Bridges the canonical Catalog configuration owner to its local ingestion projection.</summary>
public sealed class VerifiedCatalogIngestionDraftService(
    ICatalogRepository catalogRepository,
    ICatalogIngestionTargetProjectionWriter targetProjectionWriter,
    CatalogIngestionDraftService draftService,
    TimeProvider timeProvider)
{
    public async Task<CatalogIngestionCommandOutcome> ExecuteAsync(
        CatalogIngestionUpsertDraftCommand command,
        string callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var activeConfiguration = await catalogRepository.GetActiveConfigurationAsync(
            CatalogKey.Create(command.CatalogKey),
            cancellationToken)
            ?? throw new CatalogIngestionDraftException(
                "Catalog.Configuration",
                "CATALOG_INGESTION_CONFIGURATION_NOT_ACTIVE",
                409,
                $"Catalog '{command.CatalogKey}' has no active product configuration.",
                "Activate the exact Catalog configuration before importing candidate drafts.");
        if (activeConfiguration.RevisionId != command.ExpectedCatalogConfigurationRevisionId)
        {
            throw new CatalogIngestionDraftException(
                "Catalog.Configuration",
                "CATALOG_INGESTION_CONFIGURATION_STALE",
                409,
                "The command targets a Catalog configuration revision that is no longer active.",
                "Regenerate the Ingestion package against the exact active Catalog configuration.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = command.CatalogKey,
                    ["expectedConfigurationRevisionId"] = command.ExpectedCatalogConfigurationRevisionId,
                    ["activeConfigurationRevisionId"] = activeConfiguration.RevisionId,
                });
        }

        await targetProjectionWriter.UpsertAsync(
            command.SiteKey,
            command.CatalogKey,
            activeConfiguration.RevisionId,
            timeProvider.GetUtcNow(),
            cancellationToken);
        return await draftService.ExecuteAsync(
            command,
            callerIdentity,
            cancellationToken);
    }
}
