using Aggregator.Query.Application;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

public sealed partial class OverlayPreservingQueryProjectionStore
{
    private static async Task<QueryBaseProjectionComponent> ReadBaseProjectionComponentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,
                   catalog_key,
                   source_publication_id,
                   content_digest
            FROM projection.base_projection
            WHERE id = @base_projection_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "base_projection_id",
            baseProjectionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "QUERY_PUBLICATION_BASE_PROJECTION_MISSING",
                500,
                $"Publication recomposition references missing base projection '{baseProjectionId}'.",
                "Restore the exact Query base projection and replay the Catalog publication event.");
        }

        var actualCatalogKey = reader.GetString(1);
        if (!string.Equals(actualCatalogKey, catalogKey, StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_PUBLICATION_BASE_CATALOG_MISMATCH",
                500,
                $"Base projection '{baseProjectionId}' belongs to another catalog.",
                "Keep the catalog blocked and restore the exact Query base projection.");
        }

        return new QueryBaseProjectionComponent(
            reader.GetGuid(0),
            actualCatalogKey,
            reader.GetGuid(2),
            reader.GetString(3).TrimEnd());
    }
}
