using System.Data;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public static class QueryPromotionOverlayInfrastructureExtensions
{
    public static IServiceCollection AddQueryPromotionOverlayProjection(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IPromotionPlacementProjectionStore, PostgresPromotionOverlayProjectionStore>();
        return services;
    }
}

/// <summary>
/// Persists producer-owned placement changes and atomically activates a new Query-owned
/// promotion overlay and composite public-read revision.
/// </summary>
public sealed class PostgresPromotionOverlayProjectionStore : IPromotionPlacementProjectionStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IQueryIdFactory _idFactory;

    public PostgresPromotionOverlayProjectionStore(
        NpgsqlDataSource dataSource,
        IQueryIdFactory idFactory)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _idFactory = idFactory ?? throw new ArgumentNullException(nameof(idFactory));
    }

    public async Task<PromotionPlacementProjectionResult> ApplyAsync(
        QueryPromotionPlacement change,
        PromotionPlacementInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(inboxMessage);
        ValidateInbox(inboxMessage);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existingInbox = await ReadInboxAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            cancellationToken);
        if (existingInbox is not null)
        {
            if (!string.Equals(
                    existingInbox.PayloadDigest,
                    inboxMessage.PayloadDigest,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_PROMOTION_EVENT_ID_REUSED",
                    409,
                    $"Promotion event '{inboxMessage.EventId}' was already consumed with a different payload digest.",
                    "Reject the message; one event ID may identify only one exact payload.");
            }

            var replayRevision = await ReadPublicReadRevisionAsync(
                connection,
                transaction,
                existingInbox.ResultPublicReadRevisionId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PromotionPlacementProjectionResult(
                replayRevision,
                PromotionPlacementProjectionDisposition.Replayed);
        }

        var current = await ReadCurrentContextAsync(
            connection,
            transaction,
            change.CatalogKey,
            cancellationToken)
            ?? throw Failure(
                "QUERY_PUBLIC_READ_UNAVAILABLE",
                503,
                $"Catalog '{change.CatalogKey}' has no active public-read revision.",
                "Activate a complete Catalog base projection before replaying Promotion events.");

        var existingPlacement = await ReadPlacementStateAsync(
            connection,
            transaction,
            change.PlacementId,
            cancellationToken);
        if (existingPlacement is not null)
        {
            if (change.AggregateRevision < existingPlacement.PlacementRevision)
            {
                await InsertInboxAsync(
                    connection,
                    transaction,
                    change,
                    inboxMessage,
                    PromotionPlacementProjectionDisposition.IgnoredStale,
                    current.PublicReadRevision.Id,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PromotionPlacementProjectionResult(
                    current.PublicReadRevision,
                    PromotionPlacementProjectionDisposition.IgnoredStale);
            }

            if (change.AggregateRevision == existingPlacement.PlacementRevision)
            {
                if (!string.Equals(
                        existingPlacement.SourcePayloadDigest,
                        inboxMessage.PayloadDigest,
                        StringComparison.Ordinal))
                {
                    throw Failure(
                        "QUERY_PROMOTION_REVISION_PAYLOAD_CONFLICT",
                        409,
                        $"Promotion placement '{change.PlacementId}' revision '{change.AggregateRevision}' was received with a different payload digest.",
                        "Reject the message and repair the Promotion producer revision contract.");
                }

                await InsertInboxAsync(
                    connection,
                    transaction,
                    change,
                    inboxMessage,
                    PromotionPlacementProjectionDisposition.IgnoredStale,
                    current.PublicReadRevision.Id,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PromotionPlacementProjectionResult(
                    current.PublicReadRevision,
                    PromotionPlacementProjectionDisposition.IgnoredStale);
            }
        }

        if (change.IsMaterialized)
        {
            await EnsureListingExistsAsync(
                connection,
                transaction,
                current.PublicReadRevision.BaseProjectionId,
                change.ListingId,
                change.CatalogKey,
                cancellationToken);
        }

        await UpsertPlacementStateAsync(
            connection,
            transaction,
            change,
            inboxMessage.PayloadDigest,
            cancellationToken);
        var placements = await ReadMaterializedPlacementsAsync(
            connection,
            transaction,
            change.CatalogKey,
            cancellationToken);
        var sourceRevision = checked(current.PromotionSourceRevision + 1);
        var materialization = PromotionOverlayProjectionBuilder.Build(
            current.PublicReadRevision,
            current.BaseProjectionDigest,
            current.SafetyOverlayDigest,
            sourceRevision,
            placements,
            _idFactory.Create(),
            _idFactory.Create(),
            inboxMessage.ReceivedAtUtc);

        await InsertOverlayAsync(
            connection,
            transaction,
            materialization.PromotionOverlay,
            materialization.Placements,
            cancellationToken);
        await InsertPublicReadRevisionAsync(
            connection,
            transaction,
            materialization.PublicReadRevision,
            cancellationToken);
        await UpdateCurrentPointerAsync(
            connection,
            transaction,
            materialization.PublicReadRevision,
            checked(current.ActivationRevision + 1),
            inboxMessage.ReceivedAtUtc,
            cancellationToken);
        await InsertInboxAsync(
            connection,
            transaction,
            change,
            inboxMessage,
            PromotionPlacementProjectionDisposition.Activated,
            materialization.PublicReadRevision.Id,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PromotionPlacementProjectionResult(
            materialization.PublicReadRevision,
            PromotionPlacementProjectionDisposition.Activated);
    }

    private static void ValidateInbox(PromotionPlacementInboxMessage inboxMessage)
    {
        if (inboxMessage.EventId == Guid.Empty ||
            inboxMessage.ReceivedAtUtc.Offset != TimeSpan.Zero ||
            inboxMessage.PayloadDigest.Length != 64 ||
            inboxMessage.PayloadDigest.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "QUERY_PROMOTION_INBOX_INVALID",
                500,
                "Query received an invalid Promotion inbox contract.",
                "Correct the Query worker mapping before persistence.");
        }
    }

    private static async Task<PromotionInboxState?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT payload_digest, result_public_read_revision_id
            FROM messaging.promotion_inbox_message
            WHERE event_id = @event_id;
            """);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PromotionInboxState(
                reader.GetString(0).TrimEnd(),
                reader.GetGuid(1))
            : null;
    }

    private static async Task<CurrentPromotionContext?> ReadCurrentContextAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT revision.id,
                   revision.catalog_key,
                   revision.base_projection_id,
                   revision.promotion_overlay_id,
                   revision.safety_overlay_id,
                   revision.source_publication_id,
                   revision.created_at_utc,
                   revision.content_digest,
                   base.content_digest,
                   promotion.source_revision,
                   safety.content_digest,
                   current.activation_revision
            FROM projection.current_public_read current
            JOIN projection.public_read_revision revision
              ON revision.id = current.public_read_revision_id
            JOIN projection.base_projection base
              ON base.id = revision.base_projection_id
            JOIN projection.overlay_revision promotion
              ON promotion.id = revision.promotion_overlay_id
             AND promotion.kind = 'promotion'
            JOIN projection.overlay_revision safety
              ON safety.id = revision.safety_overlay_id
             AND safety.kind = 'visibility_safety'
            WHERE current.catalog_key = @catalog_key
            FOR UPDATE OF current;
            """);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Text, catalogKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var revision = RestorePublicReadRevision(reader, startOrdinal: 0);
        return new CurrentPromotionContext(
            revision,
            reader.GetString(8).TrimEnd(),
            reader.GetInt64(9),
            reader.GetString(10).TrimEnd(),
            reader.GetInt64(11));
    }

    private static async Task<PlacementState?> ReadPlacementStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid placementId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT placement_revision, source_payload_digest
            FROM projection.promotion_placement_state
            WHERE placement_id = @placement_id
            FOR UPDATE;
            """);
        command.Parameters.AddWithValue("placement_id", NpgsqlDbType.Uuid, placementId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PlacementState(
                reader.GetInt64(0),
                reader.GetString(1).TrimEnd())
            : null;
    }

    private static async Task UpsertPlacementStateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryPromotionPlacement placement,
        string payloadDigest,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO projection.promotion_placement_state
            (
                placement_id,
                entitlement_id,
                listing_id,
                catalog_key,
                product_key,
                scope_type,
                scope_key,
                locale_scope,
                starts_at_utc,
                ends_at_utc,
                hard_expiry_at_utc,
                priority_band,
                capacity_slot,
                presentation_label_key,
                state,
                placement_revision,
                source_event_occurred_at_utc,
                source_payload_digest
            )
            VALUES
            (
                @placement_id,
                @entitlement_id,
                @listing_id,
                @catalog_key,
                @product_key,
                @scope_type,
                @scope_key,
                @locale_scope,
                @starts_at_utc,
                @ends_at_utc,
                @hard_expiry_at_utc,
                @priority_band,
                @capacity_slot,
                @presentation_label_key,
                @state,
                @placement_revision,
                @source_event_occurred_at_utc,
                @source_payload_digest
            )
            ON CONFLICT (placement_id)
            DO UPDATE SET
                entitlement_id = EXCLUDED.entitlement_id,
                listing_id = EXCLUDED.listing_id,
                catalog_key = EXCLUDED.catalog_key,
                product_key = EXCLUDED.product_key,
                scope_type = EXCLUDED.scope_type,
                scope_key = EXCLUDED.scope_key,
                locale_scope = EXCLUDED.locale_scope,
                starts_at_utc = EXCLUDED.starts_at_utc,
                ends_at_utc = EXCLUDED.ends_at_utc,
                hard_expiry_at_utc = EXCLUDED.hard_expiry_at_utc,
                priority_band = EXCLUDED.priority_band,
                capacity_slot = EXCLUDED.capacity_slot,
                presentation_label_key = EXCLUDED.presentation_label_key,
                state = EXCLUDED.state,
                placement_revision = EXCLUDED.placement_revision,
                source_event_occurred_at_utc = EXCLUDED.source_event_occurred_at_utc,
                source_payload_digest = EXCLUDED.source_payload_digest;
            """);
        AddPlacementParameters(command, placement);
        command.Parameters.AddWithValue("source_payload_digest", NpgsqlDbType.Char, payloadDigest);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<QueryPromotionPlacement>> ReadMaterializedPlacementsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT placement_id,
                   entitlement_id,
                   listing_id,
                   catalog_key,
                   product_key,
                   scope_type,
                   scope_key,
                   locale_scope,
                   starts_at_utc,
                   ends_at_utc,
                   hard_expiry_at_utc,
                   priority_band,
                   capacity_slot,
                   presentation_label_key,
                   state,
                   placement_revision,
                   source_event_occurred_at_utc
            FROM projection.promotion_placement_state
            WHERE catalog_key = @catalog_key
              AND state IN ('scheduled', 'active')
            ORDER BY priority_band DESC, capacity_slot, placement_id;
            """);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Text, catalogKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var placements = new List<QueryPromotionPlacement>();
        while (await reader.ReadAsync(cancellationToken))
        {
            placements.Add(QueryPromotionPlacement.Create(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetString(4),
                ParseScope(reader.GetString(5)),
                reader.GetString(6),
                reader.GetFieldValue<string[]>(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetString(13),
                ParseState(reader.GetString(14)),
                reader.GetInt64(15),
                reader.GetFieldValue<DateTimeOffset>(16)));
        }

        return placements.AsReadOnly();
    }

    private static async Task EnsureListingExistsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid baseProjectionId,
        Guid listingId,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT EXISTS
            (
                SELECT 1
                FROM documents.listing_document document
                JOIN projection.base_projection base
                  ON base.id = document.base_projection_id
                WHERE document.base_projection_id = @base_projection_id
                  AND document.listing_id = @listing_id
                  AND base.catalog_key = @catalog_key
            );
            """);
        command.Parameters.AddWithValue("base_projection_id", NpgsqlDbType.Uuid, baseProjectionId);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, listingId);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Text, catalogKey);
        var exists = await command.ExecuteScalarAsync(cancellationToken);
        if (exists is not true)
        {
            throw Failure(
                "QUERY_PROMOTION_LISTING_NOT_IN_BASE",
                422,
                $"Promotion placement references listing '{listingId}' outside the active base projection.",
                "Publish the listing in Catalog or end the ineligible Promotion placement.");
        }
    }

    private static async Task InsertOverlayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryOverlayRevision overlay,
        IReadOnlyList<QueryPromotionPlacement> placements,
        CancellationToken cancellationToken)
    {
        await using (var command = CreateCommand(connection, transaction, """
            INSERT INTO projection.overlay_revision
            (
                id,
                catalog_key,
                kind,
                source_revision,
                created_at_utc,
                content_digest,
                item_count
            )
            VALUES
            (
                @id,
                @catalog_key,
                'promotion',
                @source_revision,
                @created_at_utc,
                @content_digest,
                @item_count
            );
            """))
        {
            command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, overlay.Id);
            command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Text, overlay.CatalogKey);
            command.Parameters.AddWithValue("source_revision", NpgsqlDbType.Bigint, overlay.SourceRevision);
            command.Parameters.AddWithValue("created_at_utc", NpgsqlDbType.TimestampTz, overlay.CreatedAtUtc);
            command.Parameters.AddWithValue("content_digest", NpgsqlDbType.Char, overlay.ContentDigest);
            command.Parameters.AddWithValue("item_count", NpgsqlDbType.Integer, overlay.ItemCount);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var placement in placements)
        {
            await using var command = CreateCommand(connection, transaction, """
                INSERT INTO projection.promotion_overlay_item
                (
                    overlay_id,
                    placement_id,
                    entitlement_id,
                    listing_id,
                    product_key,
                    scope_type,
                    scope_key,
                    locale_scope,
                    starts_at_utc,
                    ends_at_utc,
                    hard_expiry_at_utc,
                    priority_band,
                    capacity_slot,
                    presentation_label_key,
                    placement_revision
                )
                VALUES
                (
                    @overlay_id,
                    @placement_id,
                    @entitlement_id,
                    @listing_id,
                    @product_key,
                    @scope_type,
                    @scope_key,
                    @locale_scope,
                    @starts_at_utc,
                    @ends_at_utc,
                    @hard_expiry_at_utc,
                    @priority_band,
                    @capacity_slot,
                    @presentation_label_key,
                    @placement_revision
                );
                """);
            command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, overlay.Id);
            AddPlacementParameters(command, placement, includeCatalogAndState: false);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertPublicReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicReadRevision revision,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
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
            """);
        AddPublicReadRevisionParameters(command, revision);
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
        await using var command = CreateCommand(connection, transaction, """
            UPDATE projection.current_public_read
            SET public_read_revision_id = @public_read_revision_id,
                activation_revision = @activation_revision,
                activated_at_utc = @activated_at_utc
            WHERE catalog_key = @catalog_key;
            """);
        command.Parameters.AddWithValue(
            "public_read_revision_id",
            NpgsqlDbType.Uuid,
            revision.Id);
        command.Parameters.AddWithValue("activation_revision", NpgsqlDbType.Bigint, activationRevision);
        command.Parameters.AddWithValue("activated_at_utc", NpgsqlDbType.TimestampTz, activatedAtUtc);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Text, revision.CatalogKey);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_POINTER_UPDATE_FAILED",
                500,
                $"Query current public-read pointer for catalog '{revision.CatalogKey}' was not updated.",
                "Inspect the Query projection transaction and rebuild the affected catalog.");
        }
    }

    private static async Task InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        QueryPromotionPlacement change,
        PromotionPlacementInboxMessage inboxMessage,
        PromotionPlacementProjectionDisposition disposition,
        Guid resultPublicReadRevisionId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO messaging.promotion_inbox_message
            (
                event_id,
                payload_digest,
                placement_id,
                placement_revision,
                disposition,
                result_public_read_revision_id,
                received_at_utc
            )
            VALUES
            (
                @event_id,
                @payload_digest,
                @placement_id,
                @placement_revision,
                @disposition,
                @result_public_read_revision_id,
                @received_at_utc
            );
            """);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, inboxMessage.EventId);
        command.Parameters.AddWithValue("payload_digest", NpgsqlDbType.Char, inboxMessage.PayloadDigest);
        command.Parameters.AddWithValue("placement_id", NpgsqlDbType.Uuid, change.PlacementId);
        command.Parameters.AddWithValue("placement_revision", NpgsqlDbType.Bigint, change.AggregateRevision);
        command.Parameters.AddWithValue("disposition", NpgsqlDbType.Text, MapDisposition(disposition));
        command.Parameters.AddWithValue(
            "result_public_read_revision_id",
            NpgsqlDbType.Uuid,
            resultPublicReadRevisionId);
        command.Parameters.AddWithValue("received_at_utc", NpgsqlDbType.TimestampTz, inboxMessage.ReceivedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PublicReadRevision> ReadPublicReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT id,
                   catalog_key,
                   base_projection_id,
                   promotion_overlay_id,
                   safety_overlay_id,
                   source_publication_id,
                   created_at_utc,
                   content_digest
            FROM projection.public_read_revision
            WHERE id = @id;
            """);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, revisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "QUERY_PROMOTION_RESULT_REVISION_MISSING",
                500,
                $"Promotion inbox result references missing public-read revision '{revisionId}'.",
                "Restore the Query projection from authoritative Catalog and Promotion events.");
        }

        return RestorePublicReadRevision(reader, startOrdinal: 0);
    }

    private static PublicReadRevision RestorePublicReadRevision(NpgsqlDataReader reader, int startOrdinal) =>
        PublicReadRevision.Restore(
            reader.GetGuid(startOrdinal),
            reader.GetString(startOrdinal + 1),
            reader.GetGuid(startOrdinal + 2),
            reader.GetGuid(startOrdinal + 3),
            reader.GetGuid(startOrdinal + 4),
            reader.GetGuid(startOrdinal + 5),
            reader.GetFieldValue<DateTimeOffset>(startOrdinal + 6),
            reader.GetString(startOrdinal + 7).TrimEnd());

    private static void AddPlacementParameters(
        NpgsqlCommand command,
        QueryPromotionPlacement placement,
        bool includeCatalogAndState = true)
    {
        command.Parameters.AddWithValue("placement_id", NpgsqlDbType.Uuid, placement.PlacementId);
        command.Parameters.AddWithValue("entitlement_id", NpgsqlDbType.Uuid, placement.EntitlementId);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, placement.ListingId);
        if (includeCatalogAndState)
        {
            command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Text, placement.CatalogKey);
        }

        command.Parameters.AddWithValue("product_key", NpgsqlDbType.Text, placement.ProductKey);
        command.Parameters.AddWithValue("scope_type", NpgsqlDbType.Text, MapScope(placement.Scope));
        command.Parameters.AddWithValue("scope_key", NpgsqlDbType.Text, placement.ScopeKey);
        command.Parameters.AddWithValue(
            "locale_scope",
            NpgsqlDbType.Array | NpgsqlDbType.Text,
            placement.LocaleScope.ToArray());
        command.Parameters.AddWithValue("starts_at_utc", NpgsqlDbType.TimestampTz, placement.StartsAtUtc);
        command.Parameters.AddWithValue("ends_at_utc", NpgsqlDbType.TimestampTz, placement.EndsAtUtc);
        command.Parameters.AddWithValue(
            "hard_expiry_at_utc",
            NpgsqlDbType.TimestampTz,
            placement.HardExpiryAtUtc);
        command.Parameters.AddWithValue("priority_band", NpgsqlDbType.Integer, placement.PriorityBand);
        command.Parameters.AddWithValue("capacity_slot", NpgsqlDbType.Integer, placement.CapacitySlot);
        command.Parameters.AddWithValue(
            "presentation_label_key",
            NpgsqlDbType.Text,
            placement.PresentationLabelKey);
        if (includeCatalogAndState)
        {
            command.Parameters.AddWithValue("state", NpgsqlDbType.Text, MapState(placement.State));
        }

        command.Parameters.AddWithValue(
            "placement_revision",
            NpgsqlDbType.Bigint,
            placement.AggregateRevision);
        if (includeCatalogAndState)
        {
            command.Parameters.AddWithValue(
                "source_event_occurred_at_utc",
                NpgsqlDbType.TimestampTz,
                placement.OccurredAtUtc);
        }
    }

    private static void AddPublicReadRevisionParameters(
        NpgsqlCommand command,
        PublicReadRevision revision)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, revision.Id);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Text, revision.CatalogKey);
        command.Parameters.AddWithValue("base_projection_id", NpgsqlDbType.Uuid, revision.BaseProjectionId);
        command.Parameters.AddWithValue("promotion_overlay_id", NpgsqlDbType.Uuid, revision.PromotionOverlayId);
        command.Parameters.AddWithValue("safety_overlay_id", NpgsqlDbType.Uuid, revision.SafetyOverlayId);
        command.Parameters.AddWithValue("source_publication_id", NpgsqlDbType.Uuid, revision.SourcePublicationId);
        command.Parameters.AddWithValue("created_at_utc", NpgsqlDbType.TimestampTz, revision.CreatedAtUtc);
        command.Parameters.AddWithValue("content_digest", NpgsqlDbType.Char, revision.ContentDigest);
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql) =>
        new(sql, connection, transaction);

    private static string MapScope(QueryPromotionPlacementScope value) => value switch
    {
        QueryPromotionPlacementScope.Catalog => "catalog",
        QueryPromotionPlacementScope.Category => "category",
        QueryPromotionPlacementScope.District => "district",
        QueryPromotionPlacementScope.EditorialLanding => "editorial_landing",
        _ => throw Failure(
            "QUERY_PROMOTION_SCOPE_UNSUPPORTED",
            500,
            $"Query placement scope '{value}' is unsupported.",
            "Correct the Query promotion mapping before persistence."),
    };

    private static QueryPromotionPlacementScope ParseScope(string value) => value switch
    {
        "catalog" => QueryPromotionPlacementScope.Catalog,
        "category" => QueryPromotionPlacementScope.Category,
        "district" => QueryPromotionPlacementScope.District,
        "editorial_landing" => QueryPromotionPlacementScope.EditorialLanding,
        _ => throw Failure(
            "QUERY_PROMOTION_SCOPE_UNSUPPORTED",
            500,
            $"Persisted Query placement scope '{value}' is unsupported.",
            "Restore the Query projection from current Promotion events."),
    };

    private static string MapState(QueryPromotionPlacementState value) => value switch
    {
        QueryPromotionPlacementState.Scheduled => "scheduled",
        QueryPromotionPlacementState.Active => "active",
        QueryPromotionPlacementState.Paused => "paused",
        QueryPromotionPlacementState.Ended => "ended",
        QueryPromotionPlacementState.Revoked => "revoked",
        _ => throw Failure(
            "QUERY_PROMOTION_STATE_UNSUPPORTED",
            500,
            $"Query placement state '{value}' is unsupported.",
            "Correct the Query promotion mapping before persistence."),
    };

    private static QueryPromotionPlacementState ParseState(string value) => value switch
    {
        "scheduled" => QueryPromotionPlacementState.Scheduled,
        "active" => QueryPromotionPlacementState.Active,
        "paused" => QueryPromotionPlacementState.Paused,
        "ended" => QueryPromotionPlacementState.Ended,
        "revoked" => QueryPromotionPlacementState.Revoked,
        _ => throw Failure(
            "QUERY_PROMOTION_STATE_UNSUPPORTED",
            500,
            $"Persisted Query placement state '{value}' is unsupported.",
            "Restore the Query projection from current Promotion events."),
    };

    private static string MapDisposition(PromotionPlacementProjectionDisposition value) => value switch
    {
        PromotionPlacementProjectionDisposition.Activated => "activated",
        PromotionPlacementProjectionDisposition.Replayed => "replayed",
        PromotionPlacementProjectionDisposition.IgnoredStale => "ignored_stale",
        _ => throw Failure(
            "QUERY_PROMOTION_DISPOSITION_UNSUPPORTED",
            500,
            $"Query promotion disposition '{value}' is unsupported.",
            "Correct the Query promotion projection result mapping."),
    };

    private static QueryProjectionException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction) =>
        new(
            "Query.PromotionProjectionStore",
            code,
            statusCode,
            message,
            requiredAction);

    private sealed record PromotionInboxState(
        string PayloadDigest,
        Guid ResultPublicReadRevisionId);

    private sealed record PlacementState(
        long PlacementRevision,
        string SourcePayloadDigest);

    private sealed record CurrentPromotionContext(
        PublicReadRevision PublicReadRevision,
        string BaseProjectionDigest,
        long PromotionSourceRevision,
        string SafetyOverlayDigest,
        long ActivationRevision);
}
