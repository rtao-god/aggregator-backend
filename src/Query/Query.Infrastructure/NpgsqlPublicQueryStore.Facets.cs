using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

public sealed partial class NpgsqlPublicQueryStore
{
    public async Task<PublicFacetCatalogSnapshot?> ReadFacetCatalogAsync(
        string catalogKey,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        if (readAtUtc.Offset != TimeSpan.Zero)
        {
            throw StoreFailure(
                "QUERY_STORE_READ_TIMESTAMP_NOT_UTC",
                "Query facet store received a non-UTC read timestamp.",
                "Normalize the Query application clock to UTC before reading persistence.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var context = await ReadCurrentContextAsync(connection, catalogKey, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var facets = await ReadFacetsAsync(
            connection,
            context.Revision.BaseProjectionId,
            cancellationToken);
        var marketZones = await ReadMarketZoneFacetCountsAsync(
            connection,
            context.Revision.BaseProjectionId,
            cancellationToken);
        return new PublicFacetCatalogSnapshot(
            context.Revision,
            facets.CategoryCounts,
            facets.DistrictCounts,
            facets.ListingKindCounts,
            facets.ContactKindCounts,
            marketZones);
    }

    private static async Task<IReadOnlyDictionary<QueryGeographyState, int>>
        ReadMarketZoneFacetCountsAsync(
            NpgsqlConnection connection,
            Guid baseProjectionId,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT state, COUNT(*)::integer
            FROM documents.listing_geography
            WHERE base_projection_id = @base_projection_id
            GROUP BY state
            ORDER BY state;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "base_projection_id",
            baseProjectionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<QueryGeographyState, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = MapGeographyState(reader.GetString(0));
            var count = reader.GetInt32(1);
            if (count < 1 || !result.TryAdd(state, count))
            {
                throw StoreFailure(
                    "QUERY_FACET_DUPLICATE",
                    $"Query persistence contains an invalid or duplicate market-zone facet '{state}'.",
                    "Rebuild the Query projection from the sealed Catalog publication.");
            }
        }

        return result;
    }
}
