using System.Data;
using Aggregator.Promotion.Application;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Promotion.Infrastructure;

/// <summary>Promotion-owned atomic inbox and revisioned Analytics usage projection.</summary>
public sealed class PostgresPromotionUsageProjectionStore(NpgsqlDataSource dataSource)
    : IPromotionUsageProjectionStore
{
    public async Task<PromotionUsageProjectionResult> ApplyAsync(
        PromotionUsageProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "PROMOTION_USAGE_RECEIVED_TIME_NOT_UTC",
                "Promotion usage receive timestamp must be UTC.",
                500);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        await LockAsync(
            connection,
            transaction,
            $"promotion-usage-message:{change.MessageId:D}",
            cancellationToken);
        await LockAsync(
            connection,
            transaction,
            $"promotion-usage-window:{change.Projection.PlacementId:D}:{change.Projection.WindowStartsAtUtc:O}:{change.Projection.WindowEndsAtUtc:O}",
            cancellationToken);

        var inbox = await ReadInboxAsync(
            connection,
            transaction,
            change.MessageId,
            cancellationToken);
        if (inbox is not null)
        {
            EnsureExactInboxReplay(change, inbox);
            var replay = await ReadRevisionBySourceMessageAsync(
                connection,
                transaction,
                change.MessageId,
                cancellationToken) ?? throw Failure(
                    "PROMOTION_USAGE_INBOX_ORPHANED",
                    "Promotion usage inbox exists without its immutable projection revision.",
                    500);
            await transaction.CommitAsync(cancellationToken);
            return new PromotionUsageProjectionResult(
                replay,
                PromotionUsageProjectionDisposition.Duplicate);
        }

        var current = await ReadCurrentByIdentityOrWindowAsync(
            connection,
            transaction,
            change.Projection,
            cancellationToken);
        if (current is null)
        {
            if (change.Projection.SourceAggregateRevision != 1)
            {
                throw Failure(
                    "PROMOTION_USAGE_REVISION_GAP",
                    $"New Promotion usage window '{change.Projection.UsageWindowId:D}' must start at source revision 1, but received {change.Projection.SourceAggregateRevision}.",
                    409);
            }

            await InsertInboxAsync(
                connection,
                transaction,
                change,
                receivedAtUtc,
                cancellationToken);
            await InsertRevisionAsync(
                connection,
                transaction,
                change.Projection,
                receivedAtUtc,
                cancellationToken);
            await InsertCurrentAsync(
                connection,
                transaction,
                change.Projection,
                receivedAtUtc,
                cancellationToken);
        }
        else
        {
            EnsureSameWindowIdentity(current, change.Projection);
            var expectedRevision = checked(current.SourceAggregateRevision + 1);
            if (change.Projection.SourceAggregateRevision != expectedRevision)
            {
                var code = change.Projection.SourceAggregateRevision <= current.SourceAggregateRevision
                    ? "PROMOTION_USAGE_REVISION_STALE"
                    : "PROMOTION_USAGE_REVISION_GAP";
                throw Failure(
                    code,
                    $"Promotion usage window '{current.UsageWindowId:D}' expects source revision {expectedRevision}, but received {change.Projection.SourceAggregateRevision}.",
                    409);
            }

            await InsertInboxAsync(
                connection,
                transaction,
                change,
                receivedAtUtc,
                cancellationToken);
            await InsertRevisionAsync(
                connection,
                transaction,
                change.Projection,
                receivedAtUtc,
                cancellationToken);
            await UpdateCurrentAsync(
                connection,
                transaction,
                current.SourceAggregateRevision,
                change.Projection,
                receivedAtUtc,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PromotionUsageProjectionResult(
            change.Projection,
            PromotionUsageProjectionDisposition.Applied);
    }

    private static async Task LockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue("key", NpgsqlDbType.Text, key);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<InboxRow?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT contract_identity, payload_digest, correlation_id, causation_id
            FROM analytics_usage_projection.inbox_message
            WHERE message_id = @message_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, messageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new InboxRow(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private static void EnsureExactInboxReplay(
        PromotionUsageProjectionChange change,
        InboxRow inbox)
    {
        if (!string.Equals(inbox.ContractIdentity, change.ContractIdentity, StringComparison.Ordinal) ||
            !string.Equals(inbox.PayloadDigest, change.PayloadDigest, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(inbox.CorrelationId, change.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(inbox.CausationId, change.CausationId, StringComparison.Ordinal))
        {
            throw Failure(
                "PROMOTION_USAGE_INBOX_MESSAGE_CORRUPT",
                "A previously consumed Analytics message ID was replayed with different envelope data.",
                409);
        }
    }

    private static void EnsureSameWindowIdentity(
        PromotionUsageWindowProjection current,
        PromotionUsageWindowProjection incoming)
    {
        if (current.UsageWindowId != incoming.UsageWindowId ||
            current.PlacementId != incoming.PlacementId ||
            current.ListingId != incoming.ListingId ||
            !string.Equals(current.CatalogKey, incoming.CatalogKey, StringComparison.Ordinal) ||
            current.WindowStartsAtUtc != incoming.WindowStartsAtUtc ||
            current.WindowEndsAtUtc != incoming.WindowEndsAtUtc)
        {
            throw Failure(
                "PROMOTION_USAGE_WINDOW_IDENTITY_CONFLICT",
                "Analytics usage revision changes the immutable placement-window identity.",
                409);
        }
    }

    private static async Task<PromotionUsageWindowProjection?> ReadRevisionBySourceMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid sourceMessageId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT usage_window_id,
                   placement_id,
                   listing_id,
                   catalog_key,
                   window_starts_at_utc,
                   window_ends_at_utc,
                   accepted_impressions,
                   accepted_listing_opens,
                   accepted_outbound_clicks,
                   aggregation_run_id,
                   source_aggregate_revision,
                   source_message_id,
                   source_payload_digest,
                   source_occurred_at_utc
            FROM analytics_usage_projection.promotion_usage_window_revision
            WHERE source_message_id = @source_message_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("source_message_id", NpgsqlDbType.Uuid, sourceMessageId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProjection(reader) : null;
    }

    private static async Task<PromotionUsageWindowProjection?> ReadCurrentByIdentityOrWindowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionUsageWindowProjection projection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT usage_window_id,
                   placement_id,
                   listing_id,
                   catalog_key,
                   window_starts_at_utc,
                   window_ends_at_utc,
                   accepted_impressions,
                   accepted_listing_opens,
                   accepted_outbound_clicks,
                   aggregation_run_id,
                   source_aggregate_revision,
                   source_message_id,
                   source_payload_digest,
                   source_occurred_at_utc
            FROM analytics_usage_projection.promotion_usage_window
            WHERE usage_window_id = @usage_window_id
               OR (placement_id = @placement_id
                   AND window_starts_at_utc = @window_starts_at_utc
                   AND window_ends_at_utc = @window_ends_at_utc)
            ORDER BY usage_window_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("usage_window_id", NpgsqlDbType.Uuid, projection.UsageWindowId);
        command.Parameters.AddWithValue("placement_id", NpgsqlDbType.Uuid, projection.PlacementId);
        command.Parameters.AddWithValue(
            "window_starts_at_utc",
            NpgsqlDbType.TimestampTz,
            projection.WindowStartsAtUtc);
        command.Parameters.AddWithValue(
            "window_ends_at_utc",
            NpgsqlDbType.TimestampTz,
            projection.WindowEndsAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var result = ReadProjection(reader);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "PROMOTION_USAGE_WINDOW_PROJECTION_CORRUPT",
                "Promotion usage identity and placement-window key resolve to different current rows.",
                500);
        }

        return result;
    }

    private static PromotionUsageWindowProjection ReadProjection(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetGuid(9),
            reader.GetInt64(10),
            reader.GetGuid(11),
            reader.GetString(12),
            reader.GetFieldValue<DateTimeOffset>(13));

    private static async Task InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionUsageProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO analytics_usage_projection.inbox_message
            (
                message_id,
                contract_identity,
                payload_digest,
                correlation_id,
                causation_id,
                received_at_utc
            )
            VALUES
            (
                @message_id,
                @contract_identity,
                @payload_digest,
                @correlation_id,
                @causation_id,
                @received_at_utc
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, change.MessageId);
        command.Parameters.AddWithValue("contract_identity", NpgsqlDbType.Varchar, change.ContractIdentity);
        command.Parameters.AddWithValue("payload_digest", NpgsqlDbType.Char, change.PayloadDigest.ToLowerInvariant());
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, change.CorrelationId);
        command.Parameters.AddWithValue(
            "causation_id",
            NpgsqlDbType.Varchar,
            (object?)change.CausationId ?? DBNull.Value);
        command.Parameters.AddWithValue("received_at_utc", NpgsqlDbType.TimestampTz, receivedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionUsageWindowProjection projection,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO analytics_usage_projection.promotion_usage_window_revision
            (
                usage_window_id,
                source_aggregate_revision,
                placement_id,
                listing_id,
                catalog_key,
                window_starts_at_utc,
                window_ends_at_utc,
                accepted_impressions,
                accepted_listing_opens,
                accepted_outbound_clicks,
                aggregation_run_id,
                source_message_id,
                source_payload_digest,
                source_occurred_at_utc,
                applied_at_utc
            )
            VALUES
            (
                @usage_window_id,
                @source_aggregate_revision,
                @placement_id,
                @listing_id,
                @catalog_key,
                @window_starts_at_utc,
                @window_ends_at_utc,
                @accepted_impressions,
                @accepted_listing_opens,
                @accepted_outbound_clicks,
                @aggregation_run_id,
                @source_message_id,
                @source_payload_digest,
                @source_occurred_at_utc,
                @applied_at_utc
            );
            """;
        await using var command = CreateProjectionCommand(
            sql,
            connection,
            transaction,
            projection,
            appliedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionUsageWindowProjection projection,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO analytics_usage_projection.promotion_usage_window
            (
                usage_window_id,
                placement_id,
                listing_id,
                catalog_key,
                window_starts_at_utc,
                window_ends_at_utc,
                accepted_impressions,
                accepted_listing_opens,
                accepted_outbound_clicks,
                aggregation_run_id,
                source_aggregate_revision,
                source_message_id,
                source_payload_digest,
                source_occurred_at_utc,
                applied_at_utc
            )
            VALUES
            (
                @usage_window_id,
                @placement_id,
                @listing_id,
                @catalog_key,
                @window_starts_at_utc,
                @window_ends_at_utc,
                @accepted_impressions,
                @accepted_listing_opens,
                @accepted_outbound_clicks,
                @aggregation_run_id,
                @source_aggregate_revision,
                @source_message_id,
                @source_payload_digest,
                @source_occurred_at_utc,
                @applied_at_utc
            );
            """;
        await using var command = CreateProjectionCommand(
            sql,
            connection,
            transaction,
            projection,
            appliedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        long expectedCurrentRevision,
        PromotionUsageWindowProjection projection,
        DateTimeOffset appliedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE analytics_usage_projection.promotion_usage_window
            SET accepted_impressions = @accepted_impressions,
                accepted_listing_opens = @accepted_listing_opens,
                accepted_outbound_clicks = @accepted_outbound_clicks,
                aggregation_run_id = @aggregation_run_id,
                source_aggregate_revision = @source_aggregate_revision,
                source_message_id = @source_message_id,
                source_payload_digest = @source_payload_digest,
                source_occurred_at_utc = @source_occurred_at_utc,
                applied_at_utc = @applied_at_utc
            WHERE usage_window_id = @usage_window_id
              AND source_aggregate_revision = @expected_current_revision;
            """;
        await using var command = CreateProjectionCommand(
            sql,
            connection,
            transaction,
            projection,
            appliedAtUtc);
        command.Parameters.AddWithValue(
            "expected_current_revision",
            NpgsqlDbType.Bigint,
            expectedCurrentRevision);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw Failure(
                "PROMOTION_USAGE_REVISION_CONFLICT",
                "Promotion usage current revision changed while applying the Analytics event.",
                409);
        }
    }

    private static NpgsqlCommand CreateProjectionCommand(
        string sql,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionUsageWindowProjection projection,
        DateTimeOffset appliedAtUtc)
    {
        var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("usage_window_id", NpgsqlDbType.Uuid, projection.UsageWindowId);
        command.Parameters.AddWithValue("placement_id", NpgsqlDbType.Uuid, projection.PlacementId);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, projection.ListingId);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, projection.CatalogKey);
        command.Parameters.AddWithValue(
            "window_starts_at_utc",
            NpgsqlDbType.TimestampTz,
            projection.WindowStartsAtUtc);
        command.Parameters.AddWithValue(
            "window_ends_at_utc",
            NpgsqlDbType.TimestampTz,
            projection.WindowEndsAtUtc);
        command.Parameters.AddWithValue(
            "accepted_impressions",
            NpgsqlDbType.Bigint,
            projection.AcceptedImpressions);
        command.Parameters.AddWithValue(
            "accepted_listing_opens",
            NpgsqlDbType.Bigint,
            projection.AcceptedListingOpens);
        command.Parameters.AddWithValue(
            "accepted_outbound_clicks",
            NpgsqlDbType.Bigint,
            projection.AcceptedOutboundClicks);
        command.Parameters.AddWithValue(
            "aggregation_run_id",
            NpgsqlDbType.Uuid,
            projection.AggregationRunId);
        command.Parameters.AddWithValue(
            "source_aggregate_revision",
            NpgsqlDbType.Bigint,
            projection.SourceAggregateRevision);
        command.Parameters.AddWithValue(
            "source_message_id",
            NpgsqlDbType.Uuid,
            projection.SourceMessageId);
        command.Parameters.AddWithValue(
            "source_payload_digest",
            NpgsqlDbType.Char,
            projection.SourcePayloadDigest.ToLowerInvariant());
        command.Parameters.AddWithValue(
            "source_occurred_at_utc",
            NpgsqlDbType.TimestampTz,
            projection.SourceOccurredAtUtc);
        command.Parameters.AddWithValue(
            "applied_at_utc",
            NpgsqlDbType.TimestampTz,
            appliedAtUtc);
        return command;
    }

    private static PromotionApplicationException Failure(
        string code,
        string detail,
        int statusCode) =>
        new(
            "Promotion.Usage",
            code,
            statusCode,
            detail,
            "Replay or rebuild the exact Analytics usage window before continuing Promotion usage processing.");

    private sealed record InboxRow(
        string ContractIdentity,
        string PayloadDigest,
        string CorrelationId,
        string? CausationId);
}
