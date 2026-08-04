using System.Data;
using Aggregator.Promotion.Contracts;
using Aggregator.Query.Application;
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
        services.AddScoped<IPromotionOverlayProjectionStore, PostgresPromotionOverlayProjectionStore>();
        services.AddScoped<IPublicSponsoredListingStore, PostgresPublicSponsoredListingStore>();
        return services;
    }
}

public sealed class PostgresPromotionOverlayProjectionStore : IPromotionOverlayProjectionStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPromotionOverlayProjectionStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<PromotionOverlayProjectionResult> ActivateAsync(
        PromotionOverlayActivated activation,
        PromotionOverlayInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(inboxMessage);
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
                    existingInbox.Value.PayloadDigest,
                    inboxMessage.PayloadDigest,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_PROMOTION_EVENT_ID_REUSED",
                    409,
                    $"Promotion event '{inboxMessage.EventId}' was already consumed with a different payload digest.",
                    "Reject the message; an event ID may identify only one exact payload.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new PromotionOverlayProjectionResult(
                existingInbox.Value.OverlayId,
                existingInbox.Value.SourcePublicReadRevisionId,
                existingInbox.Value.ActivationRevision,
                Replayed: true,
                existingInbox.Value.StaleIgnored);
        }

        var checkpoint = await ReadCheckpointAsync(
            connection,
            transaction,
            activation.CatalogKey,
            cancellationToken);
        if (activation.ActivationRevision <= checkpoint)
        {
            await InsertInboxAsync(
                connection,
                transaction,
                activation,
                inboxMessage,
                staleIgnored: true,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PromotionOverlayProjectionResult(
                activation.OverlayId,
                activation.SourcePublicReadRevisionId,
                activation.ActivationRevision,
                Replayed: false,
                StaleIgnored: true);
        }

        await InsertOverlayAsync(connection, transaction, activation, cancellationToken);
        await UpsertCurrentAsync(connection, transaction, activation, cancellationToken);
        await UpsertCheckpointAsync(connection, transaction, activation, cancellationToken);
        await InsertInboxAsync(
            connection,
            transaction,
            activation,
            inboxMessage,
            staleIgnored: false,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PromotionOverlayProjectionResult(
            activation.OverlayId,
            activation.SourcePublicReadRevisionId,
            activation.ActivationRevision,
            Replayed: false,
            StaleIgnored: false);
    }

    private static async Task<(
        string PayloadDigest,
        Guid OverlayId,
        Guid SourcePublicReadRevisionId,
        long ActivationRevision,
        bool StaleIgnored)?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT payload_digest,
                   overlay_id,
                   source_public_read_revision_id,
                   activation_revision,
                   stale_ignored
            FROM query.promotion_overlay_inbox
            WHERE event_id = @event_id
            FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (
            reader.GetString(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetBoolean(4));
    }

    private static async Task<long> ReadCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT activation_revision
            FROM query.promotion_overlay_checkpoint
            WHERE catalog_key = @catalog_key
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : (long)value;
    }

    private static async Task InsertOverlayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionOverlayActivated activation,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("""
            INSERT INTO query.promotion_overlay_revision
            (
                overlay_id,
                catalog_key,
                source_public_read_revision_id,
                activation_revision,
                content_digest,
                created_at_utc
            )
            VALUES
            (
                @overlay_id,
                @catalog_key,
                @source_public_read_revision_id,
                @activation_revision,
                @content_digest,
                @created_at_utc
            );
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, activation.OverlayId);
            command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, activation.CatalogKey);
            command.Parameters.AddWithValue(
                "source_public_read_revision_id",
                NpgsqlDbType.Uuid,
                activation.SourcePublicReadRevisionId);
            command.Parameters.AddWithValue(
                "activation_revision",
                NpgsqlDbType.Bigint,
                activation.ActivationRevision);
            command.Parameters.AddWithValue("content_digest", NpgsqlDbType.Char, activation.ContentDigest);
            command.Parameters.AddWithValue(
                "created_at_utc",
                NpgsqlDbType.TimestampTz,
                activation.OccurredAtUtc);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in activation.Items.OrderBy(item => item.Position))
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO query.promotion_overlay_item
                (
                    overlay_id,
                    listing_id,
                    campaign_id,
                    position,
                    locale,
                    title,
                    route_path,
                    disclosure_label
                )
                VALUES
                (
                    @overlay_id,
                    @listing_id,
                    @campaign_id,
                    @position,
                    @locale,
                    @title,
                    @route_path,
                    @disclosure_label
                );
                """, connection, transaction);
            command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, activation.OverlayId);
            command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, item.ListingId);
            command.Parameters.AddWithValue("campaign_id", NpgsqlDbType.Uuid, item.CampaignId);
            command.Parameters.AddWithValue("position", NpgsqlDbType.Integer, item.Position);
            command.Parameters.AddWithValue("locale", NpgsqlDbType.Varchar, item.Locale);
            command.Parameters.AddWithValue("title", NpgsqlDbType.Varchar, item.Title);
            command.Parameters.AddWithValue("route_path", NpgsqlDbType.Varchar, item.RoutePath);
            command.Parameters.AddWithValue(
                "disclosure_label",
                NpgsqlDbType.Varchar,
                item.DisclosureLabel);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionOverlayActivated activation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO query.current_promotion_overlay
            (
                catalog_key,
                overlay_id,
                source_public_read_revision_id,
                activation_revision,
                activated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @overlay_id,
                @source_public_read_revision_id,
                @activation_revision,
                @activated_at_utc
            )
            ON CONFLICT (catalog_key)
            DO UPDATE SET
                overlay_id = EXCLUDED.overlay_id,
                source_public_read_revision_id = EXCLUDED.source_public_read_revision_id,
                activation_revision = EXCLUDED.activation_revision,
                activated_at_utc = EXCLUDED.activated_at_utc;
            """, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, activation.CatalogKey);
        command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, activation.OverlayId);
        command.Parameters.AddWithValue(
            "source_public_read_revision_id",
            NpgsqlDbType.Uuid,
            activation.SourcePublicReadRevisionId);
        command.Parameters.AddWithValue(
            "activation_revision",
            NpgsqlDbType.Bigint,
            activation.ActivationRevision);
        command.Parameters.AddWithValue(
            "activated_at_utc",
            NpgsqlDbType.TimestampTz,
            activation.OccurredAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCheckpointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionOverlayActivated activation,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO query.promotion_overlay_checkpoint
            (catalog_key, activation_revision, overlay_id, updated_at_utc)
            VALUES
            (@catalog_key, @activation_revision, @overlay_id, @updated_at_utc)
            ON CONFLICT (catalog_key)
            DO UPDATE SET
                activation_revision = EXCLUDED.activation_revision,
                overlay_id = EXCLUDED.overlay_id,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, activation.CatalogKey);
        command.Parameters.AddWithValue(
            "activation_revision",
            NpgsqlDbType.Bigint,
            activation.ActivationRevision);
        command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, activation.OverlayId);
        command.Parameters.AddWithValue(
            "updated_at_utc",
            NpgsqlDbType.TimestampTz,
            activation.OccurredAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionOverlayActivated activation,
        PromotionOverlayInboxMessage inboxMessage,
        bool staleIgnored,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO query.promotion_overlay_inbox
            (
                event_id,
                payload_digest,
                catalog_key,
                overlay_id,
                source_public_read_revision_id,
                activation_revision,
                received_at_utc,
                stale_ignored
            )
            VALUES
            (
                @event_id,
                @payload_digest,
                @catalog_key,
                @overlay_id,
                @source_public_read_revision_id,
                @activation_revision,
                @received_at_utc,
                @stale_ignored
            );
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, inboxMessage.EventId);
        command.Parameters.AddWithValue("payload_digest", NpgsqlDbType.Char, inboxMessage.PayloadDigest);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, activation.CatalogKey);
        command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, activation.OverlayId);
        command.Parameters.AddWithValue(
            "source_public_read_revision_id",
            NpgsqlDbType.Uuid,
            activation.SourcePublicReadRevisionId);
        command.Parameters.AddWithValue(
            "activation_revision",
            NpgsqlDbType.Bigint,
            activation.ActivationRevision);
        command.Parameters.AddWithValue(
            "received_at_utc",
            NpgsqlDbType.TimestampTz,
            inboxMessage.ReceivedAtUtc);
        command.Parameters.AddWithValue("stale_ignored", NpgsqlDbType.Boolean, staleIgnored);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

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
}

public interface IPublicSponsoredListingStore
{
    public Task<SponsoredListingSearchResponse?> ReadAsync(
        string catalogKey,
        Guid sourcePublicReadRevisionId,
        string locale,
        CancellationToken cancellationToken);
}

public sealed class PostgresPublicSponsoredListingStore : IPublicSponsoredListingStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPublicSponsoredListingStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<SponsoredListingSearchResponse?> ReadAsync(
        string catalogKey,
        Guid sourcePublicReadRevisionId,
        string locale,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (sourcePublicReadRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Source public read revision ID is required.", nameof(sourcePublicReadRevisionId));
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT current.overlay_id,
                   current.source_public_read_revision_id,
                   item.listing_id,
                   item.campaign_id,
                   item.position,
                   item.locale,
                   item.title,
                   item.route_path,
                   item.disclosure_label
            FROM query.current_promotion_overlay AS current
            JOIN query.promotion_overlay_item AS item
              ON item.overlay_id = current.overlay_id
            WHERE current.catalog_key = @catalog_key
              AND current.source_public_read_revision_id = @source_public_read_revision_id
              AND item.locale = @locale
            ORDER BY item.position;
            """);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        command.Parameters.AddWithValue(
            "source_public_read_revision_id",
            NpgsqlDbType.Uuid,
            sourcePublicReadRevisionId);
        command.Parameters.AddWithValue("locale", NpgsqlDbType.Varchar, locale);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Guid? overlayId = null;
        var items = new List<SponsoredListingResponse>();
        while (await reader.ReadAsync(cancellationToken))
        {
            overlayId ??= reader.GetGuid(0);
            items.Add(new SponsoredListingResponse(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetInt32(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8)));
        }

        return overlayId is null
            ? null
            : new SponsoredListingSearchResponse(
                overlayId.Value,
                sourcePublicReadRevisionId,
                items.AsReadOnly());
    }
}
