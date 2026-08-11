using System.Data;
using Aggregator.Analytics.Application;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Analytics.Infrastructure;

/// <summary>Analytics-owned PostgreSQL retention adapter for aggregate-closed interaction events.</summary>
internal sealed class PostgresAnalyticsRetentionStore(string connectionString) : IAnalyticsRetentionStore
{
    public async Task<AnalyticsRetentionBatchResult> MinimizeAsync(
        AnalyticsRetentionBatch batch,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (completedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "ANALYTICS_RETENTION_COMPLETION_TIME_NOT_UTC",
                "Analytics retention completion timestamp must be UTC.",
                "Repair the Analytics worker clock before retention resumes.");
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existing = await ReadOperationAsync(
            connection,
            transaction,
            batch.OperationId,
            cancellationToken);
        if (existing is not null)
        {
            EnsureExactReplay(batch, existing);
            await transaction.CommitAsync(cancellationToken);
            return new AnalyticsRetentionBatchResult(
                existing.OperationId,
                existing.RetainBeforeUtc,
                existing.MinimizedEventCount,
                existing.MinimizedEventCount == existing.MaximumEvents);
        }

        var candidateIds = await ClaimCandidatesAsync(
            connection,
            transaction,
            batch.RetainBeforeUtc,
            batch.MaximumEvents,
            cancellationToken);

        await InsertOperationAsync(
            connection,
            transaction,
            batch,
            candidateIds.Count,
            completedAtUtc,
            cancellationToken);

        if (candidateIds.Count > 0)
        {
            await InsertAuditAsync(
                connection,
                transaction,
                batch.OperationId,
                candidateIds,
                completedAtUtc,
                cancellationToken);
            await DeleteCampaignParametersAsync(
                connection,
                transaction,
                candidateIds,
                cancellationToken);
            await MinimizeEventsAsync(
                connection,
                transaction,
                batch.OperationId,
                candidateIds,
                completedAtUtc,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AnalyticsRetentionBatchResult(
            batch.OperationId,
            batch.RetainBeforeUtc,
            candidateIds.Count,
            candidateIds.Count == batch.MaximumEvents);
    }

    private static async Task<RetentionOperationRow?> ReadOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id,
                   request_digest,
                   retain_before_utc,
                   maximum_events,
                   minimized_event_count
            FROM operations.interaction_event_retention_operation
            WHERE id = @id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, operationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RetentionOperationRow(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt32(3),
            reader.GetInt32(4));
    }

    private static void EnsureExactReplay(
        AnalyticsRetentionBatch batch,
        RetentionOperationRow existing)
    {
        if (!string.Equals(existing.RequestDigest, batch.RequestDigest, StringComparison.OrdinalIgnoreCase) ||
            existing.RetainBeforeUtc != batch.RetainBeforeUtc ||
            existing.MaximumEvents != batch.MaximumEvents)
        {
            throw new AnalyticsCommandException(
                "Analytics.Retention",
                "ANALYTICS_RETENTION_OPERATION_ID_CONFLICT",
                409,
                "Analytics retention operation ID was replayed with different request identity.",
                "Create a new retention operation ID for a different cutoff or batch size.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["operationId"] = batch.OperationId,
                    ["expectedRequestDigest"] = existing.RequestDigest,
                    ["actualRequestDigest"] = batch.RequestDigest,
                });
        }
    }

    private static async Task<IReadOnlyList<Guid>> ClaimCandidatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DateTimeOffset retainBeforeUtc,
        int maximumEvents,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT interaction.id
            FROM events.interaction_event AS interaction
            INNER JOIN aggregates.aggregate_readiness AS readiness
                ON readiness.metric_date = (interaction.occurred_at_utc AT TIME ZONE 'UTC')::date
            WHERE interaction.retention_state = 1
              AND interaction.occurred_at_utc < @retain_before_utc
            ORDER BY interaction.occurred_at_utc, interaction.id
            LIMIT @maximum_events
            FOR UPDATE OF interaction SKIP LOCKED;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "retain_before_utc",
            NpgsqlDbType.TimestampTz,
            retainBeforeUtc);
        command.Parameters.AddWithValue(
            "maximum_events",
            NpgsqlDbType.Integer,
            maximumEvents);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>(maximumEvents);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task InsertOperationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AnalyticsRetentionBatch batch,
        int minimizedEventCount,
        DateTimeOffset completedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO operations.interaction_event_retention_operation
            (
                id,
                request_digest,
                retain_before_utc,
                maximum_events,
                minimized_event_count,
                completed_at_utc
            )
            VALUES
            (
                @id,
                @request_digest,
                @retain_before_utc,
                @maximum_events,
                @minimized_event_count,
                @completed_at_utc
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, batch.OperationId);
        command.Parameters.AddWithValue(
            "request_digest",
            NpgsqlDbType.Char,
            batch.RequestDigest.ToLowerInvariant());
        command.Parameters.AddWithValue(
            "retain_before_utc",
            NpgsqlDbType.TimestampTz,
            batch.RetainBeforeUtc);
        command.Parameters.AddWithValue(
            "maximum_events",
            NpgsqlDbType.Integer,
            batch.MaximumEvents);
        command.Parameters.AddWithValue(
            "minimized_event_count",
            NpgsqlDbType.Integer,
            minimizedEventCount);
        command.Parameters.AddWithValue(
            "completed_at_utc",
            NpgsqlDbType.TimestampTz,
            completedAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertAuditAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        IReadOnlyList<Guid> eventIds,
        DateTimeOffset retainedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO events.interaction_event_retention_audit
            (
                event_id,
                operation_id,
                client_event_id,
                event_kind,
                payload_digest,
                occurred_at_utc,
                campaign_parameter_count,
                had_placement_scope,
                retained_at_utc
            )
            SELECT interaction.id,
                   @operation_id,
                   interaction.client_event_id,
                   interaction.event_kind,
                   interaction.payload_digest,
                   interaction.occurred_at_utc,
                   (
                       SELECT count(*)::integer
                       FROM events.interaction_event_campaign_parameter AS parameter
                       WHERE parameter.event_id = interaction.id
                   ),
                   interaction.placement_scope_key IS NOT NULL,
                   @retained_at_utc
            FROM events.interaction_event AS interaction
            WHERE interaction.id = ANY(@event_ids)
            ORDER BY interaction.id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.AddWithValue(
            "event_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            eventIds.ToArray());
        command.Parameters.AddWithValue(
            "retained_at_utc",
            NpgsqlDbType.TimestampTz,
            retainedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != eventIds.Count)
        {
            throw Failure(
                "ANALYTICS_RETENTION_AUDIT_COUNT_MISMATCH",
                $"Analytics retention selected {eventIds.Count} events but persisted {affected} audit rows.",
                "Roll back the retention batch and inspect concurrent Analytics event mutation.");
        }
    }

    private static async Task DeleteCampaignParametersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<Guid> eventIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            DELETE FROM events.interaction_event_campaign_parameter
            WHERE event_id = ANY(@event_ids);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "event_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            eventIds.ToArray());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MinimizeEventsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid operationId,
        IReadOnlyList<Guid> eventIds,
        DateTimeOffset retainedAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE events.interaction_event
            SET placement_scope_key = NULL,
                retention_state = 2,
                retained_at_utc = @retained_at_utc,
                retention_operation_id = @operation_id
            WHERE id = ANY(@event_ids)
              AND retention_state = 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("operation_id", NpgsqlDbType.Uuid, operationId);
        command.Parameters.AddWithValue(
            "event_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            eventIds.ToArray());
        command.Parameters.AddWithValue(
            "retained_at_utc",
            NpgsqlDbType.TimestampTz,
            retainedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != eventIds.Count)
        {
            throw Failure(
                "ANALYTICS_RETENTION_EVENT_COUNT_MISMATCH",
                $"Analytics retention selected {eventIds.Count} events but minimized {affected} rows.",
                "Roll back the retention batch and inspect the exact event retention states.");
        }
    }

    private static AnalyticsCommandException Failure(
        string code,
        string detail,
        string requiredAction) =>
        new(
            "Analytics.Retention",
            code,
            500,
            detail,
            requiredAction);

    private sealed record RetentionOperationRow(
        Guid OperationId,
        string RequestDigest,
        DateTimeOffset RetainBeforeUtc,
        int MaximumEvents,
        int MinimizedEventCount);
}
