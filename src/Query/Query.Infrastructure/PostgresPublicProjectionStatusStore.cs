using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

/// <summary>Reads public projection status exclusively from Query-owned PostgreSQL evidence.</summary>
public sealed class PostgresPublicProjectionStatusStore(
    NpgsqlDataSource dataSource) : IPublicProjectionStatusStore
{
    public async Task<PublicProjectionStatusSnapshot?> ReadAsync(
        string catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(Sql, connection);
            command.Parameters.AddWithValue("catalog_key", catalogKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var revision = reader.IsDBNull(0)
                ? null
                : PublicReadRevision.Restore(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetGuid(2),
                    reader.GetGuid(3),
                    reader.GetGuid(4),
                    reader.GetGuid(5),
                    reader.GetFieldValue<DateTimeOffset>(6),
                    reader.GetString(7).TrimEnd());
            var snapshot = new PublicProjectionStatusSnapshot(
                catalogKey,
                revision,
                GetNullableInt64(reader, 8),
                GetNullableTimestamp(reader, 9),
                GetNullableInt64(reader, 10),
                GetNullableGuid(reader, 11),
                GetNullableGuid(reader, 12),
                GetNullableGuid(reader, 13),
                GetNullableTimestamp(reader, 14),
                reader.GetInt32(15),
                GetNullableTimestamp(reader, 16),
                GetNullableGuid(reader, 17),
                GetNullableInt32(reader, 18),
                GetNullableTimestamp(reader, 19),
                GetNullableTimestamp(reader, 20));
            if (await reader.ReadAsync(cancellationToken))
            {
                throw StoreFailure(
                    "QUERY_PROJECTION_STATUS_NOT_UNIQUE",
                    "Query projection status query returned more than one row for one catalog.",
                    catalogKey);
            }

            return snapshot;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (QueryReadException)
        {
            throw;
        }
        catch (PostgresException exception)
        {
            throw new QueryReadException(
                "Query.ProjectionStatus",
                "QUERY_PROJECTION_STATUS_SCHEMA_UNAVAILABLE",
                503,
                "Query projection status schema could not be read.",
                "Run the Query migration owner and restore the exact pointer, checkpoint, block, and sitemap schema before retrying.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = catalogKey,
                    ["sqlState"] = exception.SqlState,
                },
                exception);
        }
        catch (NpgsqlException exception)
        {
            throw new QueryReadException(
                "Query.ProjectionStatus",
                "QUERY_PROJECTION_STATUS_DATABASE_UNAVAILABLE",
                503,
                "Query projection status database is unavailable.",
                "Restore query_db connectivity without changing or repairing the public read contract.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = catalogKey,
                },
                exception);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or InvalidCastException or ArgumentException or QueryDomainException)
        {
            throw new QueryReadException(
                "Query.ProjectionStatus",
                "QUERY_PROJECTION_STATUS_ROW_INVALID",
                500,
                "Query projection status persistence returned invalid owner evidence.",
                "Inspect query_db pointer, checkpoint, block, and sitemap rows before serving projection status.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = catalogKey,
                },
                exception);
        }
    }

    private static Guid? GetNullableGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static int? GetNullableInt32(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static long? GetNullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static DateTimeOffset? GetNullableTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static QueryReadException StoreFailure(
        string code,
        string message,
        string catalogKey) =>
        new(
            "Query.ProjectionStatus",
            code,
            500,
            message,
            "Inspect query_db projection status evidence before serving the public endpoint.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = catalogKey,
            });

    private const string Sql = """
        WITH active_public_read AS
        (
            SELECT revision.id,
                   revision.catalog_key,
                   revision.base_projection_id,
                   revision.promotion_overlay_id,
                   revision.safety_overlay_id,
                   revision.source_publication_id,
                   revision.created_at_utc,
                   revision.content_digest,
                   pointer.activation_revision,
                   pointer.activated_at_utc
            FROM projection.current_public_read pointer
            JOIN projection.public_read_revision revision
              ON revision.id = pointer.public_read_revision_id
            WHERE pointer.catalog_key = @catalog_key
        ),
        source_checkpoint AS
        (
            SELECT checkpoint.last_activation_revision,
                   checkpoint.current_public_read_revision_id,
                   revision.base_projection_id,
                   revision.source_publication_id,
                   checkpoint.updated_at_utc
            FROM projection.catalog_activation_checkpoint checkpoint
            JOIN projection.public_read_revision revision
              ON revision.id = checkpoint.current_public_read_revision_id
            WHERE checkpoint.catalog_key = @catalog_key
        ),
        active_sitemap AS
        (
            SELECT pointer.public_read_revision_id,
                   revision.record_count,
                   revision.built_at_utc,
                   pointer.activated_at_utc
            FROM seo_projection.active_sitemap_revision pointer
            JOIN seo_projection.sitemap_revision revision
              ON revision.catalog_key = pointer.catalog_key
             AND revision.public_read_revision_id = pointer.public_read_revision_id
            WHERE pointer.catalog_key = @catalog_key
        ),
        active_blocks AS
        (
            SELECT count(*)::integer AS block_count,
                   min(blocked_at_utc) AS oldest_blocked_at_utc
            FROM projection.catalog_visibility_block
            WHERE catalog_key = @catalog_key
        )
        SELECT public_read.id,
               public_read.catalog_key,
               public_read.base_projection_id,
               public_read.promotion_overlay_id,
               public_read.safety_overlay_id,
               public_read.source_publication_id,
               public_read.created_at_utc,
               public_read.content_digest,
               public_read.activation_revision,
               public_read.activated_at_utc,
               checkpoint.last_activation_revision,
               checkpoint.current_public_read_revision_id,
               checkpoint.base_projection_id,
               checkpoint.source_publication_id,
               checkpoint.updated_at_utc,
               blocks.block_count,
               blocks.oldest_blocked_at_utc,
               sitemap.public_read_revision_id,
               sitemap.record_count,
               sitemap.built_at_utc,
               sitemap.activated_at_utc
        FROM active_blocks blocks
        LEFT JOIN active_public_read public_read ON TRUE
        LEFT JOIN source_checkpoint checkpoint ON TRUE
        LEFT JOIN active_sitemap sitemap ON TRUE
        WHERE public_read.id IS NOT NULL
           OR checkpoint.last_activation_revision IS NOT NULL
           OR blocks.block_count > 0
           OR sitemap.public_read_revision_id IS NOT NULL;
        """;
}
