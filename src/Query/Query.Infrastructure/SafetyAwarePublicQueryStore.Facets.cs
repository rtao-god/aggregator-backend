using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public sealed partial class SafetyAwarePublicQueryStore
{
    public async Task<PublicFacetCatalogSnapshot?> ReadAsync(
        string catalogKey,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        if (readAtUtc.Offset != TimeSpan.Zero)
        {
            throw StoreFailure(
                "QUERY_SAFETY_CLOCK_NOT_UTC",
                "Safety-aware Query facet store received a non-UTC timestamp.",
                "Configure the Query public-read clock to return UTC timestamps.");
        }

        var raw = await _inner.ReadFacetCatalogAsync(
            catalogKey,
            readAtUtc,
            cancellationToken);
        if (raw is null)
        {
            return null;
        }

        _ = await LoadSafetyAsync(raw.Revision, readAtUtc, cancellationToken);
        var safetyFacets = await ReadFacetCountsAsync(
            raw.Revision,
            readAtUtc,
            cancellationToken);
        var marketZones = await ReadSafetyMarketZoneFacetCountsAsync(
            raw.Revision,
            readAtUtc,
            cancellationToken);
        return new PublicFacetCatalogSnapshot(
            raw.Revision,
            safetyFacets.CategoryCounts,
            safetyFacets.DistrictCounts,
            safetyFacets.ListingKindCounts,
            safetyFacets.ContactKindCounts,
            marketZones);
    }

    private async Task<IReadOnlyDictionary<QueryGeographyState, int>>
        ReadSafetyMarketZoneFacetCountsAsync(
            PublicReadRevision revision,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureCatalogNotBlockedAsync(connection, revision.CatalogKey, cancellationToken);
        const string sql = """
            SELECT geography.state,
                   count(*)::integer
            FROM documents.listing_geography geography
            WHERE geography.base_projection_id = @base_projection_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item item
                  WHERE item.overlay_id = @safety_overlay_id
                    AND item.target_kind = 'listing'
                    AND item.listing_id = geography.listing_id
                    AND item.starts_at_utc <= @read_at_utc
                    AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM documents.listing_localization localization
                  JOIN projection.visibility_safety_overlay_item item
                    ON item.overlay_id = @safety_overlay_id
                   AND item.target_kind = 'route'
                   AND item.target_key = localization.route_path
                   AND item.starts_at_utc <= @read_at_utc
                   AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
                  WHERE localization.base_projection_id = geography.base_projection_id
                    AND localization.listing_id = geography.listing_id
              )
            GROUP BY geography.state
            ORDER BY geography.state;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "base_projection_id",
            revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "safety_overlay_id",
            revision.SafetyOverlayId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "read_at_utc",
            readAtUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<QueryGeographyState, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = NpgsqlPublicQueryStore.MapGeographyState(reader.GetString(0));
            var count = reader.GetInt32(1);
            if (count < 1 || !result.TryAdd(state, count))
            {
                throw StoreFailure(
                    "QUERY_SAFETY_FACET_INVALID",
                    $"Safety-filtered market-zone facet '{state}' is invalid or duplicated.",
                    "Rebuild the exact Query projection and safety overlay.");
            }
        }

        return result;
    }
}
