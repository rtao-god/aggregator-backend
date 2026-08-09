using System.Data;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Reads and verifies only the event-backed Ingestion-local Catalog configuration projection.</summary>
public sealed class PostgresCatalogIngestionReferenceReader(IngestionDbContext dbContext)
    : ICatalogIngestionReferenceReader
{
    public async Task<CatalogIngestionReference?> GetAsync(
        string siteKey,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siteKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT
                    reference.site_key,
                    reference.catalog_key,
                    reference.active_configuration_revision_id,
                    reference.configuration_digest,
                    reference.market_area_key,
                    reference.supported_listing_kinds,
                    reference.aggregate_revision,
                    reference.source_event_id,
                    reference.source_payload_digest,
                    reference.activated_at_utc,
                    reference.projection_digest,
                    reference.updated_at_utc,
                    inbox.routing_key,
                    inbox.contract_identity,
                    inbox.payload_digest,
                    inbox.site_key,
                    inbox.catalog_key,
                    inbox.configuration_revision_id,
                    inbox.previous_configuration_revision_id,
                    inbox.aggregate_revision,
                    inbox.correlation_id,
                    inbox.projection_digest
                FROM catalog_projection.catalog_reference AS reference
                INNER JOIN messaging.catalog_configuration_inbox AS inbox
                    ON inbox.message_id = reference.source_event_id
                WHERE reference.site_key = @site_key
                  AND reference.catalog_key = @catalog_key;
                """,
                connection);
            command.Parameters.Add(new NpgsqlParameter<string>("site_key", siteKey));
            command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var row = new StoredReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetFieldValue<int[]>(5),
                reader.GetInt64(6),
                reader.GetGuid(7),
                reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11),
                reader.GetString(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetString(15),
                reader.GetString(16),
                reader.GetGuid(17),
                reader.IsDBNull(18) ? null : reader.GetGuid(18),
                reader.GetInt64(19),
                reader.GetString(20),
                reader.GetString(21));
            return ValidateAndMap(row);
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static CatalogIngestionReference ValidateAndMap(StoredReference row)
    {
        if (!IsKey(row.SiteKey) || !IsKey(row.CatalogKey) || !IsKey(row.MarketAreaKey))
        {
            throw ProjectionCorrupt(row, "A projected site, catalog, or market-area key is invalid.");
        }

        if (row.ConfigurationRevisionId == Guid.Empty ||
            row.SourceEventId == Guid.Empty ||
            row.AggregateRevision <= 0)
        {
            throw ProjectionCorrupt(
                row,
                "The active configuration identity, source event identity, and aggregate revision must be present.");
        }

        if (!IsDigest(row.ConfigurationDigest) ||
            !IsDigest(row.SourcePayloadDigest) ||
            !IsDigest(row.ProjectionDigest))
        {
            throw ProjectionCorrupt(row, "A projected content or lineage digest is invalid.");
        }

        if (row.ActivatedAtUtc.Offset != TimeSpan.Zero ||
            row.UpdatedAtUtc.Offset != TimeSpan.Zero ||
            row.UpdatedAtUtc < row.ActivatedAtUtc)
        {
            throw ProjectionCorrupt(row, "Projection timestamps are non-UTC or out of order.");
        }

        if (row.SupportedListingKinds is null || row.SupportedListingKinds.Length == 0)
        {
            throw ProjectionCorrupt(row, "At least one supported public listing kind is required.");
        }

        var listingKinds = new List<IngestionEntityKindContract>(row.SupportedListingKinds.Length);
        var previousRawKind = 0;
        foreach (var rawKind in row.SupportedListingKinds)
        {
            if (rawKind <= previousRawKind ||
                !Enum.IsDefined(typeof(IngestionEntityKindContract), rawKind))
            {
                throw ProjectionCorrupt(
                    row,
                    $"Listing kind value '{rawKind}' is unsupported, duplicated, or not in canonical order.");
            }

            var kind = (IngestionEntityKindContract)rawKind;
            if (kind is not IngestionEntityKindContract.Place and not IngestionEntityKindContract.Provider)
            {
                throw ProjectionCorrupt(row, $"Entity kind '{kind}' cannot be a public listing kind.");
            }

            listingKinds.Add(kind);
            previousRawKind = rawKind;
        }

        if (!string.Equals(
                row.InboxRoutingKey,
                CatalogIntegrationEventTypes.ConfigurationActivated,
                StringComparison.Ordinal) ||
            !string.Equals(
                row.InboxContractIdentity,
                CatalogIntegrationEventContracts.ConfigurationActivated,
                StringComparison.Ordinal) ||
            !string.Equals(row.InboxPayloadDigest, row.SourcePayloadDigest, StringComparison.Ordinal) ||
            !string.Equals(row.InboxSiteKey, row.SiteKey, StringComparison.Ordinal) ||
            !string.Equals(row.InboxCatalogKey, row.CatalogKey, StringComparison.Ordinal) ||
            row.InboxConfigurationRevisionId != row.ConfigurationRevisionId ||
            row.InboxAggregateRevision != row.AggregateRevision ||
            !string.Equals(row.InboxProjectionDigest, row.ProjectionDigest, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(row.InboxCorrelationId))
        {
            throw ProjectionCorrupt(
                row,
                "The current projection and its producer-event inbox record have divergent identities or effects.");
        }

        if ((row.AggregateRevision == 1 && row.PreviousConfigurationRevisionId is not null) ||
            (row.AggregateRevision > 1 && row.PreviousConfigurationRevisionId is null))
        {
            throw ProjectionCorrupt(row, "The activation revision and previous configuration pointer are inconsistent.");
        }

        var computedProjectionDigest = CatalogConfigurationProjectionDigest.Compute(
            row.SiteKey,
            row.CatalogKey,
            row.ConfigurationRevisionId,
            row.PreviousConfigurationRevisionId,
            row.ConfigurationDigest,
            row.MarketAreaKey,
            listingKinds,
            row.AggregateRevision,
            row.SourceEventId,
            row.SourcePayloadDigest,
            row.ActivatedAtUtc);
        if (!string.Equals(computedProjectionDigest, row.ProjectionDigest, StringComparison.Ordinal))
        {
            throw ProjectionCorrupt(
                row,
                $"Projection digest mismatch. Stored '{row.ProjectionDigest}', computed '{computedProjectionDigest}'.");
        }

        return new CatalogIngestionReference(
            row.SiteKey,
            row.CatalogKey,
            row.ConfigurationRevisionId,
            listingKinds.AsReadOnly(),
            row.AggregateRevision);
    }

    private static bool IsKey(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 96 &&
        value[0] is >= 'a' and <= 'z' &&
        value[^1] != '-' &&
        !value.Contains("--", StringComparison.Ordinal) &&
        value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    private static bool IsDigest(string value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static IngestionApplicationException ProjectionCorrupt(
        StoredReference row,
        string detail) =>
        new(
            "Ingestion.CatalogProjection",
            "INGESTION_CATALOG_PROJECTION_CORRUPT",
            503,
            $"Catalog projection '{row.SiteKey}/{row.CatalogKey}' is invalid. {detail}",
            "Replay the exact producer-owned Catalog configuration events or rebuild the Ingestion Catalog projection.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["siteKey"] = row.SiteKey,
                ["catalogKey"] = row.CatalogKey,
                ["aggregateRevision"] = row.AggregateRevision,
                ["activeConfigurationRevisionId"] = row.ConfigurationRevisionId,
                ["sourceEventId"] = row.SourceEventId,
            });

    private sealed record StoredReference(
        string SiteKey,
        string CatalogKey,
        Guid ConfigurationRevisionId,
        string ConfigurationDigest,
        string MarketAreaKey,
        int[] SupportedListingKinds,
        long AggregateRevision,
        Guid SourceEventId,
        string SourcePayloadDigest,
        DateTimeOffset ActivatedAtUtc,
        string ProjectionDigest,
        DateTimeOffset UpdatedAtUtc,
        string InboxRoutingKey,
        string InboxContractIdentity,
        string InboxPayloadDigest,
        string InboxSiteKey,
        string InboxCatalogKey,
        Guid InboxConfigurationRevisionId,
        Guid? PreviousConfigurationRevisionId,
        long InboxAggregateRevision,
        string InboxCorrelationId,
        string InboxProjectionDigest);
}
