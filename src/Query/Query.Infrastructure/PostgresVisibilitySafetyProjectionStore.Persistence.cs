using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public sealed partial class PostgresVisibilitySafetyProjectionStore
{
    private static async Task<PersistedInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT payload_digest,
                   processing_state,
                   result_public_read_revision_id
            FROM messaging.visibility_suppression_inbox_message
            WHERE event_id = @event_id
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", eventId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PersistedInbox(
            reader.GetString(0).TrimEnd(),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2));
    }

    private static async Task InsertPendingInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO messaging.visibility_suppression_inbox_message
            (
                event_id,
                payload_digest,
                catalog_key,
                suppression_id,
                suppression_revision,
                processing_state,
                result_public_read_revision_id,
                received_at_utc,
                processed_at_utc
            )
            VALUES
            (
                @event_id,
                @payload_digest,
                @catalog_key,
                @suppression_id,
                @suppression_revision,
                'pending',
                NULL,
                @received_at_utc,
                NULL
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        AddInboxOwnerParameters(command, suppression, inboxMessage);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertFinalInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        string processingState,
        Guid resultPublicReadRevisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO messaging.visibility_suppression_inbox_message
            (
                event_id,
                payload_digest,
                catalog_key,
                suppression_id,
                suppression_revision,
                processing_state,
                result_public_read_revision_id,
                received_at_utc,
                processed_at_utc
            )
            VALUES
            (
                @event_id,
                @payload_digest,
                @catalog_key,
                @suppression_id,
                @suppression_revision,
                @processing_state,
                @result_public_read_revision_id,
                @received_at_utc,
                @received_at_utc
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        AddInboxOwnerParameters(command, suppression, inboxMessage);
        command.Parameters.Add(new NpgsqlParameter<string>("processing_state", processingState));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "result_public_read_revision_id",
            resultPublicReadRevisionId));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CompleteInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        string processingState,
        Guid resultPublicReadRevisionId,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE messaging.visibility_suppression_inbox_message
            SET processing_state = @processing_state,
                result_public_read_revision_id = @result_public_read_revision_id,
                processed_at_utc = @processed_at_utc
            WHERE event_id = @event_id
              AND processing_state = 'pending';
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("processing_state", processingState));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "result_public_read_revision_id",
            resultPublicReadRevisionId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("processed_at_utc", processedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", eventId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_VISIBILITY_INBOX_COMPLETION_CONFLICT",
                409,
                $"Visibility inbox event '{eventId}' was not pending during completion.",
                "Reload the durable Query inbox state before retrying the event.");
        }
    }

    private static void AddInboxOwnerParameters(
        NpgsqlCommand command,
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>("event_id", inboxMessage.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", inboxMessage.PayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", suppression.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>("suppression_id", suppression.SuppressionId));
        command.Parameters.Add(new NpgsqlParameter<long>(
            "suppression_revision",
            suppression.AggregateRevision));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "received_at_utc",
            inboxMessage.ReceivedAtUtc));
    }

    private static async Task EnsureBlockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        string reasonCode,
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
                blocked_at_utc
            )
            VALUES
            (
                @block_id,
                @catalog_key,
                @source_event_id,
                @suppression_id,
                @suppression_revision,
                @payload_digest,
                @reason_code,
                @blocked_at_utc
            )
            ON CONFLICT (source_event_id)
            DO UPDATE SET
                payload_digest = EXCLUDED.payload_digest,
                reason_code = EXCLUDED.reason_code,
                blocked_at_utc = EXCLUDED.blocked_at_utc;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("block_id", inboxMessage.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", suppression.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", inboxMessage.EventId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("suppression_id", suppression.SuppressionId));
        command.Parameters.Add(new NpgsqlParameter<long>(
            "suppression_revision",
            suppression.AggregateRevision));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", inboxMessage.PayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<string>("reason_code", reasonCode));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "blocked_at_utc",
            inboxMessage.ReceivedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteBlockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceEventId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM projection.catalog_visibility_block
            WHERE source_event_id = @source_event_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", sourceEventId));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_VISIBILITY_BLOCK_MISSING",
                500,
                $"Visibility block for event '{sourceEventId}' disappeared before overlay activation.",
                "Restore the Query visibility block and replay the exact Catalog event.");
        }
    }

    private static async Task<PersistedSuppressionState?> ReadSuppressionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid suppressionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT suppression_id,
                   catalog_key,
                   target_kind,
                   listing_id,
                   target_key,
                   public_reason_class,
                   response_mode,
                   starts_at_utc,
                   expires_at_utc,
                   state,
                   aggregate_revision,
                   occurred_at_utc,
                   source_payload_digest
            FROM projection.visibility_suppression_state
            WHERE suppression_id = @suppression_id
            """ + (forUpdate ? " FOR UPDATE;" : ";");
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("suppression_id", suppressionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var value = QueryVisibilitySuppression.Create(
            reader.GetGuid(0),
            reader.GetString(1),
            ParseTargetKind(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            ParseResponseMode(reader.GetString(6)),
            ParseState(reader.GetString(9)),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetInt64(10),
            reader.GetFieldValue<DateTimeOffset>(11));
        return new PersistedSuppressionState(value, reader.GetString(12).TrimEnd());
    }

    private static async Task<PublicReadRevision?> LoadCurrentPublicReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        bool lockPointer,
        CancellationToken cancellationToken)
    {
        var sql = """
            SELECT revision.id,
                   revision.catalog_key,
                   revision.base_projection_id,
                   revision.promotion_overlay_id,
                   revision.safety_overlay_id,
                   revision.source_publication_id,
                   revision.created_at_utc,
                   revision.content_digest
            FROM projection.current_public_read current_read
            JOIN projection.public_read_revision revision
              ON revision.id = current_read.public_read_revision_id
            WHERE current_read.catalog_key = @catalog_key
            """ + (lockPointer ? " FOR UPDATE OF current_read;" : ";");
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadPublicReadRevision(reader)
            : null;
    }

    private static async Task<CurrentReadContext?> ReadCurrentContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT revision.id,
                   revision.catalog_key,
                   revision.base_projection_id,
                   revision.promotion_overlay_id,
                   revision.safety_overlay_id,
                   revision.source_publication_id,
                   revision.created_at_utc,
                   revision.content_digest,
                   base_projection.content_digest,
                   promotion_overlay.content_digest,
                   safety_overlay.source_revision,
                   current_read.activation_revision
            FROM projection.current_public_read current_read
            JOIN projection.public_read_revision revision
              ON revision.id = current_read.public_read_revision_id
            JOIN projection.base_projection base_projection
              ON base_projection.id = revision.base_projection_id
            JOIN projection.overlay_revision promotion_overlay
              ON promotion_overlay.id = revision.promotion_overlay_id
             AND promotion_overlay.kind = 'promotion'
            JOIN projection.overlay_revision safety_overlay
              ON safety_overlay.id = revision.safety_overlay_id
             AND safety_overlay.kind = 'visibility_safety'
            WHERE current_read.catalog_key = @catalog_key
            FOR UPDATE OF current_read;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CurrentReadContext(
            ReadPublicReadRevision(reader),
            reader.GetString(8).TrimEnd(),
            reader.GetString(9).TrimEnd(),
            reader.GetInt64(10),
            reader.GetInt64(11));
    }

    private static async Task<PublicReadRevision> LoadPublicReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,
                   catalog_key,
                   base_projection_id,
                   promotion_overlay_id,
                   safety_overlay_id,
                   source_publication_id,
                   created_at_utc,
                   content_digest
            FROM projection.public_read_revision
            WHERE id = @revision_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("revision_id", revisionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "QUERY_VISIBILITY_RESULT_REVISION_MISSING",
                500,
                $"Visibility inbox references missing public-read revision '{revisionId}'.",
                "Restore the Query database from owner backup or rebuild the visibility projection.");
        }

        return ReadPublicReadRevision(reader);
    }

    private static PublicReadRevision ReadPublicReadRevision(System.Data.Common.DbDataReader reader) =>
        PublicReadRevision.Restore(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7).TrimEnd());

    private static async Task EnsureActiveTargetExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        QueryVisibilitySuppression suppression,
        CancellationToken cancellationToken)
    {
        if (suppression.TargetKind is
            QueryVisibilitySuppressionTargetKind.Listing or
            QueryVisibilitySuppressionTargetKind.Media or
            QueryVisibilitySuppressionTargetKind.Route)
        {
            return;
        }

        if (suppression.TargetKind == QueryVisibilitySuppressionTargetKind.ExternalReference)
        {
            throw Failure(
                "QUERY_VISIBILITY_EXTERNAL_REFERENCE_UNSUPPORTED",
                422,
                "External-reference suppression cannot be materialized because the current publication contract exposes no stable external-reference identity.",
                "Keep the catalog blocked and add the Catalog-owned external-reference identity to the publication contract.");
        }

        if (suppression.TargetKind != QueryVisibilitySuppressionTargetKind.Contact)
        {
            throw Failure(
                "QUERY_VISIBILITY_TARGET_KIND_UNSUPPORTED",
                422,
                $"Visibility target kind '{suppression.TargetKind}' is unsupported.",
                "Republish a supported Catalog visibility target.");
        }

        if (!Guid.TryParse(suppression.TargetKey, out var contactId) || contactId == Guid.Empty)
        {
            throw Failure(
                "QUERY_VISIBILITY_CONTACT_IDENTITY_INVALID",
                422,
                "Contact suppression target is not a non-empty UUID.",
                "Republish the Catalog suppression with the exact producer-owned contact ID.");
        }

        const string sql = """
            SELECT EXISTS
            (
                SELECT 1
                FROM documents.listing_contact
                WHERE base_projection_id = @base_projection_id
                  AND contact_id = @contact_id
            );
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("contact_id", contactId));
        var exists = await command.ExecuteScalarAsync(cancellationToken);
        if (exists is not true)
        {
            throw Failure(
                "QUERY_VISIBILITY_CONTACT_TARGET_MISSING",
                422,
                $"Contact '{contactId}' is absent from base projection '{baseProjectionId}'.",
                "Keep the catalog blocked and replay the suppression only after the exact Catalog publication is projected.");
        }
    }

    private static async Task UpsertSuppressionStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO projection.visibility_suppression_state
            (
                suppression_id,
                catalog_key,
                target_kind,
                listing_id,
                target_key,
                public_reason_class,
                response_mode,
                starts_at_utc,
                expires_at_utc,
                state,
                aggregate_revision,
                occurred_at_utc,
                source_event_id,
                source_payload_digest
            )
            VALUES
            (
                @suppression_id,
                @catalog_key,
                @target_kind,
                @listing_id,
                @target_key,
                @public_reason_class,
                @response_mode,
                @starts_at_utc,
                @expires_at_utc,
                @state,
                @aggregate_revision,
                @occurred_at_utc,
                @source_event_id,
                @source_payload_digest
            )
            ON CONFLICT (suppression_id)
            DO UPDATE SET
                state = EXCLUDED.state,
                aggregate_revision = EXCLUDED.aggregate_revision,
                occurred_at_utc = EXCLUDED.occurred_at_utc,
                source_event_id = EXCLUDED.source_event_id,
                source_payload_digest = EXCLUDED.source_payload_digest;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        AddSuppressionParameters(command, suppression);
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", inboxMessage.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "source_payload_digest",
            inboxMessage.PayloadDigest));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<QueryVisibilitySuppression>> ReadActiveSuppressionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT suppression_id,
                   catalog_key,
                   target_kind,
                   listing_id,
                   target_key,
                   public_reason_class,
                   response_mode,
                   starts_at_utc,
                   expires_at_utc,
                   aggregate_revision,
                   occurred_at_utc
            FROM projection.visibility_suppression_state
            WHERE catalog_key = @catalog_key
              AND state = 'active'
            ORDER BY suppression_id;
            """;
        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<QueryVisibilitySuppression>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(QueryVisibilitySuppression.Create(
                reader.GetGuid(0),
                reader.GetString(1),
                ParseTargetKind(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseResponseMode(reader.GetString(6)),
                QueryVisibilitySuppressionState.Active,
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetInt64(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return result;
    }

    private static async Task InsertOverlayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VisibilitySafetyProjectionMaterialization materialization,
        CancellationToken cancellationToken)
    {
        const string overlaySql = """
            INSERT INTO projection.overlay_revision
                (id, catalog_key, kind, source_revision, created_at_utc, content_digest, item_count)
            VALUES
                (@id, @catalog_key, 'visibility_safety', @source_revision, @created_at_utc, @content_digest, @item_count);
            """;
        await using (var command = CreateCommand(connection, transaction, overlaySql))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("id", materialization.Overlay.Id));
            command.Parameters.Add(new NpgsqlParameter<string>(
                "catalog_key",
                materialization.Overlay.CatalogKey));
            command.Parameters.Add(new NpgsqlParameter<long>(
                "source_revision",
                materialization.Overlay.SourceRevision));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
                "created_at_utc",
                materialization.Overlay.CreatedAtUtc));
            command.Parameters.Add(new NpgsqlParameter<string>(
                "content_digest",
                materialization.Overlay.ContentDigest));
            command.Parameters.Add(new NpgsqlParameter<int>(
                "item_count",
                materialization.Overlay.ItemCount));
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        const string itemSql = """
            INSERT INTO projection.visibility_safety_overlay_item
            (
                overlay_id,
                suppression_id,
                target_kind,
                listing_id,
                target_key,
                public_reason_class,
                response_mode,
                starts_at_utc,
                expires_at_utc,
                aggregate_revision,
                occurred_at_utc
            )
            VALUES
            (
                @overlay_id,
                @suppression_id,
                @target_kind,
                @listing_id,
                @target_key,
                @public_reason_class,
                @response_mode,
                @starts_at_utc,
                @expires_at_utc,
                @aggregate_revision,
                @occurred_at_utc
            );
            """;
        foreach (var suppression in materialization.ActiveSuppressions)
        {
            await using var command = CreateCommand(connection, transaction, itemSql);
            command.Parameters.Add(new NpgsqlParameter<Guid>("overlay_id", materialization.Overlay.Id));
            AddSuppressionParameters(command, suppression);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
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
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "public_read_revision_id",
            revision.Id));
        command.Parameters.Add(new NpgsqlParameter<long>("activation_revision", activationRevision));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("activated_at_utc", activatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", revision.CatalogKey));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "QUERY_VISIBILITY_POINTER_MISSING",
                503,
                $"Catalog '{revision.CatalogKey}' lost its current public-read pointer during safety activation.",
                "Restore the Query pointer; the catalog visibility block must remain until recovery.");
        }
    }

    private static void AddSuppressionParameters(
        NpgsqlCommand command,
        QueryVisibilitySuppression suppression)
    {
        command.Parameters.Add(new NpgsqlParameter<Guid>("suppression_id", suppression.SuppressionId));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", suppression.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<string>("target_kind", MapTargetKind(suppression.TargetKind)));
        command.Parameters.Add(new NpgsqlParameter("listing_id", NpgsqlDbType.Uuid)
        {
            Value = suppression.ListingId is { } listingId ? listingId : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter<string>("target_key", suppression.TargetKey));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "public_reason_class",
            suppression.PublicReasonClass));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "response_mode",
            MapResponseMode(suppression.ResponseMode)));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "starts_at_utc",
            suppression.StartsAtUtc));
        command.Parameters.Add(new NpgsqlParameter("expires_at_utc", NpgsqlDbType.TimestampTz)
        {
            Value = suppression.ExpiresAtUtc is { } expiresAtUtc ? expiresAtUtc : DBNull.Value,
        });
        command.Parameters.Add(new NpgsqlParameter<string>("state", MapState(suppression.State)));
        command.Parameters.Add(new NpgsqlParameter<long>(
            "aggregate_revision",
            suppression.AggregateRevision));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "occurred_at_utc",
            suppression.OccurredAtUtc));
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string commandText) =>
        new(commandText, connection, transaction);

    private static string MapTargetKind(QueryVisibilitySuppressionTargetKind value)
    {
        return value switch
        {
            QueryVisibilitySuppressionTargetKind.Listing => "listing",
            QueryVisibilitySuppressionTargetKind.Media => "media",
            QueryVisibilitySuppressionTargetKind.Contact => "contact",
            QueryVisibilitySuppressionTargetKind.Route => "route",
            QueryVisibilitySuppressionTargetKind.ExternalReference => "external_reference",
            _ => throw Failure(
                "QUERY_VISIBILITY_TARGET_KIND_UNSUPPORTED",
                500,
                $"Visibility target kind '{value}' is unsupported by persistence.",
                "Correct the Query visibility persistence mapper."),
        };
    }

    private static QueryVisibilitySuppressionTargetKind ParseTargetKind(string value)
    {
        return value switch
        {
            "listing" => QueryVisibilitySuppressionTargetKind.Listing,
            "media" => QueryVisibilitySuppressionTargetKind.Media,
            "contact" => QueryVisibilitySuppressionTargetKind.Contact,
            "route" => QueryVisibilitySuppressionTargetKind.Route,
            "external_reference" => QueryVisibilitySuppressionTargetKind.ExternalReference,
            _ => throw Failure(
                "QUERY_VISIBILITY_TARGET_KIND_UNSUPPORTED",
                500,
                $"Visibility target kind '{value}' is unsupported in persistence.",
                "Restore or migrate the Query visibility state through its owner contract."),
        };
    }

    private static string MapResponseMode(QueryVisibilitySuppressionResponseMode value)
    {
        return value switch
        {
            QueryVisibilitySuppressionResponseMode.HideAsNotFound => "hide_as_not_found",
            QueryVisibilitySuppressionResponseMode.Gone => "gone",
            QueryVisibilitySuppressionResponseMode.TemporarilyUnavailable => "temporarily_unavailable",
            QueryVisibilitySuppressionResponseMode.OmitChildElement => "omit_child_element",
            _ => throw Failure(
                "QUERY_VISIBILITY_RESPONSE_MODE_UNSUPPORTED",
                500,
                $"Visibility response mode '{value}' is unsupported by persistence.",
                "Correct the Query visibility persistence mapper."),
        };
    }

    private static QueryVisibilitySuppressionResponseMode ParseResponseMode(string value)
    {
        return value switch
        {
            "hide_as_not_found" => QueryVisibilitySuppressionResponseMode.HideAsNotFound,
            "gone" => QueryVisibilitySuppressionResponseMode.Gone,
            "temporarily_unavailable" => QueryVisibilitySuppressionResponseMode.TemporarilyUnavailable,
            "omit_child_element" => QueryVisibilitySuppressionResponseMode.OmitChildElement,
            _ => throw Failure(
                "QUERY_VISIBILITY_RESPONSE_MODE_UNSUPPORTED",
                500,
                $"Visibility response mode '{value}' is unsupported in persistence.",
                "Restore or migrate the Query visibility state through its owner contract."),
        };
    }

    private static string MapState(QueryVisibilitySuppressionState value)
    {
        return value switch
        {
            QueryVisibilitySuppressionState.Active => "active",
            QueryVisibilitySuppressionState.Resolved => "resolved",
            _ => throw Failure(
                "QUERY_VISIBILITY_STATE_UNSUPPORTED",
                500,
                $"Visibility suppression state '{value}' is unsupported by persistence.",
                "Correct the Query visibility persistence mapper."),
        };
    }

    private static QueryVisibilitySuppressionState ParseState(string value)
    {
        return value switch
        {
            "active" => QueryVisibilitySuppressionState.Active,
            "resolved" => QueryVisibilitySuppressionState.Resolved,
            _ => throw Failure(
                "QUERY_VISIBILITY_STATE_UNSUPPORTED",
                500,
                $"Visibility suppression state '{value}' is unsupported in persistence.",
                "Restore or migrate the Query visibility state through its owner contract."),
        };
    }

    private sealed record PersistedSuppressionState(
        QueryVisibilitySuppression Value,
        string PayloadDigest);
}
