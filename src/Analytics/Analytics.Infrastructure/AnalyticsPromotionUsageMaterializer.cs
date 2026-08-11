using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Analytics.Infrastructure;

/// <summary>Materializes revisioned Promotion usage and its outbox message inside the active aggregation transaction.</summary>
internal sealed class AnalyticsPromotionUsageMaterializer(
    AnalyticsDbContext dbContext,
    IAnalyticsIdSource idSource)
{
    public async Task<int> MaterializeAsync(
        AnalyticsAggregationLease lease,
        RebuildDailyAnalyticsMetricsRequest request,
        IReadOnlyList<AnalyticsAggregateInteractionProjection> eventRows,
        DateTimeOffset materializedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(eventRows);
        if (materializedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_MATERIALIZED_TIME_NOT_UTC",
                "Promotion usage materialization time must be UTC.");
        }

        if (request.FromInclusive != lease.FromInclusive ||
            request.ToExclusive != lease.ToExclusive)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_LEASE_RANGE_MISMATCH",
                "Promotion usage request does not match the exact aggregation lease range.");
        }

        var observations = new List<AcceptedSponsoredInteraction>();
        foreach (var row in eventRows)
        {
            if ((PlacementExposureKind)row.PlacementExposureKind != PlacementExposureKind.Sponsored)
            {
                continue;
            }

            if (row.ListingId is not { } listingId ||
                row.PlacementId is not { } placementId ||
                listingId == Guid.Empty ||
                placementId == Guid.Empty)
            {
                throw Failure(
                    "ANALYTICS_PROMOTION_USAGE_EVENT_IDENTITY_CORRUPT",
                    $"Sponsored interaction '{row.Id:D}' lacks its exact listing or placement identity.");
            }

            observations.Add(new AcceptedSponsoredInteraction(
                row.Id,
                (InteractionEventKind)row.EventKind,
                row.CatalogKey,
                listingId,
                placementId,
                row.OccurredAtUtc,
                row.PayloadDigest));
        }

        var derived = PromotionUsageWindowDeriver.Derive(
            observations,
            request.FromInclusive,
            request.ToExclusive);
        var connection = dbContext.Database.GetDbConnection() as NpgsqlConnection
            ?? throw Failure(
                "ANALYTICS_PROMOTION_USAGE_CONNECTION_INVALID",
                "Analytics aggregation does not use the required PostgreSQL connection adapter.");
        var transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction
            ?? throw Failure(
                "ANALYTICS_PROMOTION_USAGE_TRANSACTION_MISSING",
                "Promotion usage materialization requires the active Analytics aggregation transaction.");
        var rangeStartsAtUtc = ToUtcStart(request.FromInclusive);
        var rangeEndsAtUtc = ToUtcStart(request.ToExclusive);
        var currentRows = await ReadCurrentAsync(
            connection,
            transaction,
            rangeStartsAtUtc,
            rangeEndsAtUtc,
            cancellationToken);
        var currentByKey = currentRows.ToDictionary(ToKey);
        var derivedByKey = derived.ToDictionary(ToKey);
        var keys = currentByKey.Keys
            .Union(derivedByKey.Keys)
            .OrderBy(item => item.WindowStartsAtUtc)
            .ThenBy(item => item.PlacementId)
            .ToArray();
        var emitted = 0;
        foreach (var key in keys)
        {
            currentByKey.TryGetValue(key, out var current);
            if (!derivedByKey.TryGetValue(key, out var candidate))
            {
                if (current is null)
                {
                    continue;
                }

                candidate = PromotionUsageWindowDeriver.CreateZeroCorrection(
                    current.PlacementId,
                    current.ListingId,
                    current.CatalogKey,
                    current.WindowStartsAtUtc,
                    current.WindowEndsAtUtc);
            }

            if (current is not null)
            {
                EnsureSameIdentity(current, candidate);
                if (string.Equals(
                        current.SourceDigest,
                        candidate.SourceDigest,
                        StringComparison.OrdinalIgnoreCase))
                {
                    EnsureSameValues(current, candidate);
                    continue;
                }
            }

            var usageWindowId = current?.UsageWindowId ?? idSource.CreateId();
            var aggregateRevision = current is null
                ? 1
                : checked(current.AggregateRevision + 1);
            var eventId = idSource.CreateId();
            var closedWindow = new ClosedPromotionUsageWindow(
                usageWindowId,
                candidate.PlacementId,
                candidate.ListingId,
                candidate.CatalogKey,
                candidate.WindowStartsAtUtc,
                candidate.WindowEndsAtUtc,
                candidate.AcceptedImpressions,
                candidate.AcceptedListingOpens,
                candidate.AcceptedOutboundClicks,
                lease.RunId,
                aggregateRevision);
            var message = PromotionUsageOutboxMessageFactory.Create(
                closedWindow,
                eventId,
                materializedAtUtc,
                $"analytics-aggregation:{lease.RunId:D}",
                lease.RunId);
            await InsertOutboxAsync(
                connection,
                transaction,
                message,
                cancellationToken);
            await InsertRevisionAsync(
                connection,
                transaction,
                candidate,
                usageWindowId,
                aggregateRevision,
                lease.RunId,
                message,
                materializedAtUtc,
                cancellationToken);
            if (current is null)
            {
                await InsertCurrentAsync(
                    connection,
                    transaction,
                    candidate,
                    usageWindowId,
                    aggregateRevision,
                    lease.RunId,
                    message,
                    materializedAtUtc,
                    cancellationToken);
            }
            else
            {
                await UpdateCurrentAsync(
                    connection,
                    transaction,
                    candidate,
                    usageWindowId,
                    current.AggregateRevision,
                    aggregateRevision,
                    lease.RunId,
                    message,
                    materializedAtUtc,
                    cancellationToken);
            }

            emitted++;
        }

        return emitted;
    }

    private static async Task<IReadOnlyList<CurrentUsageWindow>> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset rangeStartsAtUtc,
        DateTimeOffset rangeEndsAtUtc,
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
                   source_digest,
                   aggregate_revision
            FROM aggregates.promotion_usage_window
            WHERE window_starts_at_utc >= @range_starts_at_utc
              AND window_ends_at_utc <= @range_ends_at_utc
            ORDER BY window_starts_at_utc, placement_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "range_starts_at_utc",
            NpgsqlDbType.TimestampTz,
            rangeStartsAtUtc);
        command.Parameters.AddWithValue(
            "range_ends_at_utc",
            NpgsqlDbType.TimestampTz,
            rangeEndsAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<CurrentUsageWindow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CurrentUsageWindow(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetString(9),
                reader.GetInt64(10)));
        }

        return result;
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AnalyticsPromotionUsageOutboxMessage message,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO messaging.outbox_message
            (
                message_id,
                routing_key,
                contract_identity,
                payload_json,
                payload_digest,
                occurred_at_utc,
                correlation_id,
                causation_id
            )
            VALUES
            (
                @message_id,
                @routing_key,
                @contract_identity,
                @payload_json,
                @payload_digest,
                @occurred_at_utc,
                @correlation_id,
                @causation_id
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, message.MessageId);
        command.Parameters.AddWithValue("routing_key", NpgsqlDbType.Varchar, message.RoutingKey);
        command.Parameters.AddWithValue("contract_identity", NpgsqlDbType.Varchar, message.ContractIdentity);
        command.Parameters.AddWithValue("payload_json", NpgsqlDbType.Text, message.PayloadJson);
        command.Parameters.AddWithValue("payload_digest", NpgsqlDbType.Char, message.PayloadDigest);
        command.Parameters.AddWithValue("occurred_at_utc", NpgsqlDbType.TimestampTz, message.OccurredAtUtc);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, message.CorrelationId);
        command.Parameters.AddWithValue(
            "causation_id",
            NpgsqlDbType.Varchar,
            (object?)message.CausationId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DerivedPromotionUsageWindow candidate,
        Guid usageWindowId,
        long aggregateRevision,
        Guid aggregationRunId,
        AnalyticsPromotionUsageOutboxMessage message,
        DateTimeOffset materializedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO aggregates.promotion_usage_window_revision
            (
                usage_window_id,
                aggregate_revision,
                placement_id,
                listing_id,
                catalog_key,
                window_starts_at_utc,
                window_ends_at_utc,
                accepted_impressions,
                accepted_listing_opens,
                accepted_outbound_clicks,
                source_digest,
                aggregation_run_id,
                source_message_id,
                materialized_at_utc
            )
            VALUES
            (
                @usage_window_id,
                @aggregate_revision,
                @placement_id,
                @listing_id,
                @catalog_key,
                @window_starts_at_utc,
                @window_ends_at_utc,
                @accepted_impressions,
                @accepted_listing_opens,
                @accepted_outbound_clicks,
                @source_digest,
                @aggregation_run_id,
                @source_message_id,
                @materialized_at_utc
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("usage_window_id", NpgsqlDbType.Uuid, usageWindowId);
        command.Parameters.AddWithValue("aggregate_revision", NpgsqlDbType.Bigint, aggregateRevision);
        command.Parameters.AddWithValue("placement_id", NpgsqlDbType.Uuid, candidate.PlacementId);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, candidate.ListingId);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, candidate.CatalogKey);
        command.Parameters.AddWithValue("window_starts_at_utc", NpgsqlDbType.TimestampTz, candidate.WindowStartsAtUtc);
        command.Parameters.AddWithValue("window_ends_at_utc", NpgsqlDbType.TimestampTz, candidate.WindowEndsAtUtc);
        command.Parameters.AddWithValue("accepted_impressions", NpgsqlDbType.Bigint, candidate.AcceptedImpressions);
        command.Parameters.AddWithValue("accepted_listing_opens", NpgsqlDbType.Bigint, candidate.AcceptedListingOpens);
        command.Parameters.AddWithValue("accepted_outbound_clicks", NpgsqlDbType.Bigint, candidate.AcceptedOutboundClicks);
        command.Parameters.AddWithValue("source_digest", NpgsqlDbType.Char, candidate.SourceDigest);
        command.Parameters.AddWithValue("aggregation_run_id", NpgsqlDbType.Uuid, aggregationRunId);
        command.Parameters.AddWithValue("source_message_id", NpgsqlDbType.Uuid, message.MessageId);
        command.Parameters.AddWithValue("materialized_at_utc", NpgsqlDbType.TimestampTz, materializedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DerivedPromotionUsageWindow candidate,
        Guid usageWindowId,
        long aggregateRevision,
        Guid aggregationRunId,
        AnalyticsPromotionUsageOutboxMessage message,
        DateTimeOffset materializedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO aggregates.promotion_usage_window
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
                source_digest,
                aggregation_run_id,
                aggregate_revision,
                source_message_id,
                materialized_at_utc
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
                @source_digest,
                @aggregation_run_id,
                @aggregate_revision,
                @source_message_id,
                @materialized_at_utc
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddCurrentParameters(
            command,
            candidate,
            usageWindowId,
            aggregateRevision,
            aggregationRunId,
            message,
            materializedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DerivedPromotionUsageWindow candidate,
        Guid usageWindowId,
        long expectedAggregateRevision,
        long aggregateRevision,
        Guid aggregationRunId,
        AnalyticsPromotionUsageOutboxMessage message,
        DateTimeOffset materializedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE aggregates.promotion_usage_window
            SET accepted_impressions = @accepted_impressions,
                accepted_listing_opens = @accepted_listing_opens,
                accepted_outbound_clicks = @accepted_outbound_clicks,
                source_digest = @source_digest,
                aggregation_run_id = @aggregation_run_id,
                aggregate_revision = @aggregate_revision,
                source_message_id = @source_message_id,
                materialized_at_utc = @materialized_at_utc
            WHERE usage_window_id = @usage_window_id
              AND aggregate_revision = @expected_aggregate_revision;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddCurrentParameters(
            command,
            candidate,
            usageWindowId,
            aggregateRevision,
            aggregationRunId,
            message,
            materializedAtUtc);
        command.Parameters.AddWithValue(
            "expected_aggregate_revision",
            NpgsqlDbType.Bigint,
            expectedAggregateRevision);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_REVISION_CONFLICT",
                "Promotion usage current row changed during aggregate materialization.");
        }
    }

    private static void AddCurrentParameters(
        NpgsqlCommand command,
        DerivedPromotionUsageWindow candidate,
        Guid usageWindowId,
        long aggregateRevision,
        Guid aggregationRunId,
        AnalyticsPromotionUsageOutboxMessage message,
        DateTimeOffset materializedAtUtc)
    {
        command.Parameters.AddWithValue("usage_window_id", NpgsqlDbType.Uuid, usageWindowId);
        command.Parameters.AddWithValue("placement_id", NpgsqlDbType.Uuid, candidate.PlacementId);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, candidate.ListingId);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, candidate.CatalogKey);
        command.Parameters.AddWithValue("window_starts_at_utc", NpgsqlDbType.TimestampTz, candidate.WindowStartsAtUtc);
        command.Parameters.AddWithValue("window_ends_at_utc", NpgsqlDbType.TimestampTz, candidate.WindowEndsAtUtc);
        command.Parameters.AddWithValue("accepted_impressions", NpgsqlDbType.Bigint, candidate.AcceptedImpressions);
        command.Parameters.AddWithValue("accepted_listing_opens", NpgsqlDbType.Bigint, candidate.AcceptedListingOpens);
        command.Parameters.AddWithValue("accepted_outbound_clicks", NpgsqlDbType.Bigint, candidate.AcceptedOutboundClicks);
        command.Parameters.AddWithValue("source_digest", NpgsqlDbType.Char, candidate.SourceDigest);
        command.Parameters.AddWithValue("aggregation_run_id", NpgsqlDbType.Uuid, aggregationRunId);
        command.Parameters.AddWithValue("aggregate_revision", NpgsqlDbType.Bigint, aggregateRevision);
        command.Parameters.AddWithValue("source_message_id", NpgsqlDbType.Uuid, message.MessageId);
        command.Parameters.AddWithValue("materialized_at_utc", NpgsqlDbType.TimestampTz, materializedAtUtc);
    }

    private static UsageWindowKey ToKey(DerivedPromotionUsageWindow window) =>
        new(window.PlacementId, window.WindowStartsAtUtc, window.WindowEndsAtUtc);

    private static UsageWindowKey ToKey(CurrentUsageWindow window) =>
        new(window.PlacementId, window.WindowStartsAtUtc, window.WindowEndsAtUtc);

    private static void EnsureSameIdentity(
        CurrentUsageWindow current,
        DerivedPromotionUsageWindow candidate)
    {
        if (current.PlacementId != candidate.PlacementId ||
            current.ListingId != candidate.ListingId ||
            !string.Equals(current.CatalogKey, candidate.CatalogKey, StringComparison.Ordinal) ||
            current.WindowStartsAtUtc != candidate.WindowStartsAtUtc ||
            current.WindowEndsAtUtc != candidate.WindowEndsAtUtc)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_IDENTITY_CONFLICT",
                "Promotion usage stream identity changed while rebuilding its exact window.");
        }
    }

    private static void EnsureSameValues(
        CurrentUsageWindow current,
        DerivedPromotionUsageWindow candidate)
    {
        if (current.AcceptedImpressions != candidate.AcceptedImpressions ||
            current.AcceptedListingOpens != candidate.AcceptedListingOpens ||
            current.AcceptedOutboundClicks != candidate.AcceptedOutboundClicks)
        {
            throw Failure(
                "ANALYTICS_PROMOTION_USAGE_DIGEST_CONFLICT",
                "Promotion usage source digest was reused for different accepted counts.");
        }
    }

    private static DateTimeOffset ToUtcStart(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    private static AnalyticsCommandException Failure(string code, string detail) =>
        new(
            "Analytics.PromotionUsage",
            code,
            500,
            detail,
            "Stop aggregation and repair the exact Analytics usage stream before retrying.");

    private sealed record UsageWindowKey(
        Guid PlacementId,
        DateTimeOffset WindowStartsAtUtc,
        DateTimeOffset WindowEndsAtUtc);

    private sealed record CurrentUsageWindow(
        Guid UsageWindowId,
        Guid PlacementId,
        Guid ListingId,
        string CatalogKey,
        DateTimeOffset WindowStartsAtUtc,
        DateTimeOffset WindowEndsAtUtc,
        long AcceptedImpressions,
        long AcceptedListingOpens,
        long AcceptedOutboundClicks,
        string SourceDigest,
        long AggregateRevision);
}
