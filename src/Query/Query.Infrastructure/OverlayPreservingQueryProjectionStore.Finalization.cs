using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

public sealed partial class OverlayPreservingQueryProjectionStore
{
    private static async Task<CurrentActivation?> ReadCurrentActivationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT revision.id,
                   revision.base_projection_id,
                   revision.source_publication_id,
                   current_read.activation_revision
            FROM projection.current_public_read current_read
            JOIN projection.public_read_revision revision
              ON revision.id = current_read.public_read_revision_id
            WHERE current_read.catalog_key = @catalog_key
            FOR UPDATE OF current_read;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CurrentActivation(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetInt64(3))
            : null;
    }

    private static async Task<QueryOverlayRevision> ReadOverlayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid overlayId,
        QueryOverlayKind expectedKind,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,
                   catalog_key,
                   kind,
                   source_revision,
                   created_at_utc,
                   content_digest,
                   item_count
            FROM projection.overlay_revision
            WHERE id = @overlay_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("overlay_id", overlayId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "QUERY_PUBLICATION_OVERLAY_MISSING",
                500,
                $"Publication recomposition references missing overlay '{overlayId}'.",
                "Restore the exact immutable overlay and replay the Catalog publication event.");
        }

        var actualCatalogKey = reader.GetString(1);
        var actualKind = reader.GetString(2) switch
        {
            "promotion" => QueryOverlayKind.Promotion,
            "visibility_safety" => QueryOverlayKind.VisibilitySafety,
            var unsupported => throw Failure(
                "QUERY_PUBLICATION_OVERLAY_KIND_UNSUPPORTED",
                500,
                $"Publication recomposition overlay '{overlayId}' has unsupported kind '{unsupported}'.",
                "Restore the Query overlay through its owner migration."),
        };
        if (actualKind != expectedKind ||
            !string.Equals(actualCatalogKey, catalogKey, StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_PUBLICATION_OVERLAY_IDENTITY_INVALID",
                500,
                $"Publication recomposition overlay '{overlayId}' has the wrong owner identity.",
                "Keep the catalog blocked and restore the exact Query overlay components.");
        }

        return QueryOverlayRevision.Create(
            reader.GetGuid(0),
            actualCatalogKey,
            actualKind,
            reader.GetInt64(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetString(5).TrimEnd(),
            reader.GetInt32(6));
    }

    private static async Task DeletePublicationBlockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM projection.catalog_visibility_block
            WHERE source_event_id = @source_event_id
              AND block_kind = 'publication_recomposition';
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", sourceEventId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_PUBLICATION_BLOCK_MISSING",
                500,
                $"Catalog publication event '{sourceEventId}' lost its fail-closed visibility block.",
                "Restore the publication recomposition block before exposing public traffic.");
        }
    }

    private static async Task DeleteRecompositionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM projection.publication_overlay_recomposition
            WHERE source_event_id = @source_event_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", sourceEventId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_PUBLICATION_RECOMPOSITION_DELETE_FAILED",
                500,
                $"Catalog publication event '{sourceEventId}' recomposition state could not be completed.",
                "Keep the visibility block and inspect Query projection persistence.");
        }
    }
}
