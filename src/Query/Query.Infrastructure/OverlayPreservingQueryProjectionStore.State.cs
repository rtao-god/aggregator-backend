using Aggregator.Query.Application;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public sealed partial class OverlayPreservingQueryProjectionStore
{
    private static async Task<PublicationRecompositionState?> ReadRecompositionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEventId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT source_event_id,
                   catalog_key,
                   payload_digest,
                   previous_public_read_revision_id,
                   previous_pointer_activation_revision,
                   promotion_overlay_id,
                   safety_overlay_id,
                   created_at_utc
            FROM projection.publication_overlay_recomposition
            WHERE source_event_id = @source_event_id
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", sourceEventId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PublicationRecompositionState(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2).TrimEnd(),
            reader.GetGuid(3),
            reader.GetInt64(4),
            reader.GetGuid(5),
            reader.GetGuid(6),
            reader.GetFieldValue<DateTimeOffset>(7));
    }

    private static async Task<Guid?> ReadCatalogRecompositionEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_event_id
            FROM projection.publication_overlay_recomposition
            WHERE catalog_key = @catalog_key
            FOR UPDATE;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid eventId ? eventId : null;
    }

    private static async Task<string?> ReadPublicationInboxDigestAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT payload_digest
            FROM messaging.inbox_message
            WHERE event_id = @event_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", eventId));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string digest ? digest.TrimEnd() : null;
    }

    private static async Task<CurrentComponents?> ReadCurrentComponentsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT revision.id,
                   current_read.activation_revision,
                   revision.promotion_overlay_id,
                   revision.safety_overlay_id
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
            ? new CurrentComponents(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetGuid(2),
                reader.GetGuid(3))
            : null;
    }

    private static async Task EnsurePromotionOverlayCompatibleWithBuildAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid promotionOverlayId,
        HashSet<Guid> newListingIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(newListingIds);
        const string sql = """
            SELECT placement_id, listing_id
            FROM projection.promotion_overlay_item
            WHERE overlay_id = @promotion_overlay_id
            ORDER BY placement_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "promotion_overlay_id",
            promotionOverlayId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var listingId = reader.GetGuid(1);
            if (newListingIds.Contains(listingId))
            {
                continue;
            }

            throw Failure(
                "QUERY_PUBLICATION_PROMOTION_LISTING_REMOVED",
                503,
                $"Promotion placement '{reader.GetGuid(0)}' references listing '{listingId}' absent from the incoming Catalog publication.",
                "End or pause the ineligible Promotion placement, then replay the Catalog publication event.");
        }
    }

    private static async Task InsertRecompositionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicationRecompositionState state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.publication_overlay_recomposition
            (
                source_event_id,
                catalog_key,
                payload_digest,
                previous_public_read_revision_id,
                previous_pointer_activation_revision,
                promotion_overlay_id,
                safety_overlay_id,
                created_at_utc
            )
            VALUES
            (
                @source_event_id,
                @catalog_key,
                @payload_digest,
                @previous_public_read_revision_id,
                @previous_pointer_activation_revision,
                @promotion_overlay_id,
                @safety_overlay_id,
                @created_at_utc
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", state.SourceEventId));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", state.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", state.PayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "previous_public_read_revision_id",
            state.PreviousPublicReadRevisionId));
        command.Parameters.Add(new NpgsqlParameter<long>(
            "previous_pointer_activation_revision",
            state.PreviousPointerActivationRevision));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "promotion_overlay_id",
            state.PromotionOverlayId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "safety_overlay_id",
            state.SafetyOverlayId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at_utc", state.CreatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertPublicationBlockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicationRecompositionState state,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.catalog_visibility_block
            (
                block_id,
                catalog_key,
                source_event_id,
                suppression_id,
                suppression_revision,
                payload_digest,
                reason_code,
                blocked_at_utc,
                block_kind
            )
            VALUES
            (
                @block_id,
                @catalog_key,
                @source_event_id,
                NULL,
                NULL,
                @payload_digest,
                'publication_overlay_recomposition_pending',
                @blocked_at_utc,
                'publication_recomposition'
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("block_id", state.SourceEventId));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", state.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", state.SourceEventId));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", state.PayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("blocked_at_utc", state.CreatedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText) =>
        new(commandText, connection, transaction);
}
