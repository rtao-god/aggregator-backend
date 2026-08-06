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

    private static async Task EnsurePromotionOverlayCompatibleAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid promotionOverlayId,
        Guid baseProjectionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item.placement_id, item.listing_id
            FROM projection.promotion_overlay_item item
            LEFT JOIN documents.listing_document document
              ON document.base_projection_id = @base_projection_id
             AND document.listing_id = item.listing_id
            WHERE item.overlay_id = @promotion_overlay_id
              AND document.listing_id IS NULL
            ORDER BY item.placement_id
            LIMIT 1;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "promotion_overlay_id",
            promotionOverlayId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "QUERY_PUBLICATION_PROMOTION_LISTING_REMOVED",
                500,
                $"Promotion placement '{reader.GetGuid(0)}' references listing '{reader.GetGuid(1)}' absent from the materialized Catalog base projection.",
                "Keep the catalog blocked and repair the Query base materialization before replaying the publication event.");
        }
    }

    private static async Task InsertPublicReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.public_read_revision
            (
                id,
                catalog_key,
                base_projection_id,
                promotion_overlay_id,
                safety_overlay_id,
                source_publication_id,
                created_at_utc,
                content_digest
            )
            VALUES
            (
                @id,
                @catalog_key,
                @base_projection_id,
                @promotion_overlay_id,
                @safety_overlay_id,
                @source_publication_id,
                @created_at_utc,
                @content_digest
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", revision.Id));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", revision.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "promotion_overlay_id",
            revision.PromotionOverlayId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("safety_overlay_id", revision.SafetyOverlayId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "source_publication_id",
            revision.SourcePublicationId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at_utc", revision.CreatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("content_digest", revision.ContentDigest));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateCurrentPointerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        long activationRevision,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE projection.current_public_read
            SET public_read_revision_id = @public_read_revision_id,
                activation_revision = @activation_revision,
                activated_at_utc = @activated_at_utc
            WHERE catalog_key = @catalog_key;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("public_read_revision_id", revision.Id));
        command.Parameters.Add(new NpgsqlParameter<long>("activation_revision", activationRevision));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("activated_at_utc", activatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", revision.CatalogKey));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_PUBLICATION_POINTER_UPDATE_FAILED",
                500,
                $"Catalog '{revision.CatalogKey}' public-read pointer could not activate the recomposed revision.",
                "Keep the visibility block and replay the Catalog publication event.");
        }
    }

    private static async Task UpdatePublicationCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        QueryInboxMessage inboxMessage,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE projection.catalog_activation_checkpoint
            SET current_public_read_revision_id = @public_read_revision_id,
                updated_at_utc = @updated_at_utc
            WHERE catalog_key = @catalog_key
              AND last_event_id = @event_id
              AND last_payload_digest = @payload_digest;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("public_read_revision_id", revision.Id));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("updated_at_utc", updatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", revision.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", inboxMessage.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", inboxMessage.PayloadDigest));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_PUBLICATION_CHECKPOINT_UPDATE_FAILED",
                500,
                $"Catalog publication event '{inboxMessage.EventId}' checkpoint no longer matches its exact payload.",
                "Keep the visibility block and inspect Query publication inbox/checkpoint consistency.");
        }
    }

    private static async Task UpdatePublicationInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid publicReadRevisionId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE messaging.inbox_message
            SET result_public_read_revision_id = @public_read_revision_id
            WHERE event_id = @event_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "public_read_revision_id",
            publicReadRevisionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", eventId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_PUBLICATION_INBOX_UPDATE_FAILED",
                500,
                $"Catalog publication event '{eventId}' inbox result could not be recomposed.",
                "Keep the visibility block and replay the exact Catalog publication event.");
        }
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
