using Npgsql;
using NpgsqlTypes;

namespace Platform.Messaging;

/// <summary>Leases durable messages in a short transaction, publishes outside it, and records exact delivery.</summary>
public sealed class PostgresOutboxDispatcher
{
    private readonly OutboxDispatcherOptions _options;
    private readonly IIntegrationEventPublisher _publisher;

    public PostgresOutboxDispatcher(
        OutboxDispatcherOptions options,
        IIntegrationEventPublisher publisher)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
    }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var leaseToken = Guid.CreateVersion7();
        var messages = await LeaseAsync(leaseToken, cancellationToken);
        foreach (var message in messages)
        {
            try
            {
                await _publisher.PublishAsync(message, cancellationToken);
                await MarkDispatchedAsync(message.MessageId, leaseToken, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await MarkFailedAsync(message.MessageId, leaseToken, exception, cancellationToken);
                throw;
            }
        }

        return messages.Count;
    }

    private async Task<IReadOnlyList<OutboxMessage>> LeaseAsync(Guid leaseToken, CancellationToken cancellationToken)
    {
        var table = $"{_options.Schema}.outbox_message";
        var sql = $"""
            with claim as (
                select message_id
                from {table}
                where dispatched_at_utc is null
                  and (lease_expires_at_utc is null or lease_expires_at_utc <= @now)
                order by occurred_at_utc, message_id
                for update skip locked
                limit @batchSize
            )
            update {table} message
            set lease_token = @leaseToken,
                leased_by = @leasedBy,
                lease_expires_at_utc = @leaseExpiresAtUtc,
                delivery_attempts = delivery_attempts + 1
            from claim
            where message.message_id = claim.message_id
            returning message.message_id,
                      message.routing_key,
                      message.contract_identity,
                      message.payload_json,
                      message.payload_digest,
                      message.occurred_at_utc,
                      message.correlation_id,
                      message.causation_id;
            """;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var now = DateTimeOffset.UtcNow;
        command.Parameters.AddWithValue("now", NpgsqlDbType.TimestampTz, now);
        command.Parameters.AddWithValue("batchSize", NpgsqlDbType.Integer, _options.BatchSize);
        command.Parameters.AddWithValue("leaseToken", NpgsqlDbType.Uuid, leaseToken);
        command.Parameters.AddWithValue("leasedBy", NpgsqlDbType.Text, _options.DispatcherIdentity);
        command.Parameters.AddWithValue("leaseExpiresAtUtc", NpgsqlDbType.TimestampTz, now + _options.LeaseDuration);

        var result = new List<OutboxMessage>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(new OutboxMessage(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5),
                    reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetGuid(7)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task MarkDispatchedAsync(Guid messageId, Guid leaseToken, CancellationToken cancellationToken)
    {
        var table = $"{_options.Schema}.outbox_message";
        var sql = $"""
            update {table}
            set dispatched_at_utc = @dispatchedAtUtc,
                lease_token = null,
                leased_by = null,
                lease_expires_at_utc = null,
                last_error = null
            where message_id = @messageId
              and lease_token = @leaseToken
              and dispatched_at_utc is null;
            """;
        await ExecuteTransitionAsync(sql, messageId, leaseToken, null, cancellationToken);
    }

    private async Task MarkFailedAsync(
        Guid messageId,
        Guid leaseToken,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var table = $"{_options.Schema}.outbox_message";
        var sql = $"""
            update {table}
            set lease_token = null,
                leased_by = null,
                lease_expires_at_utc = null,
                last_error = @lastError
            where message_id = @messageId
              and lease_token = @leaseToken
              and dispatched_at_utc is null;
            """;
        var error = $"{exception.GetType().Name}: {exception.Message}";
        if (error.Length > 2000)
        {
            error = error[..2000];
        }

        await ExecuteTransitionAsync(sql, messageId, leaseToken, error, cancellationToken);
    }

    private async Task ExecuteTransitionAsync(
        string sql,
        Guid messageId,
        Guid leaseToken,
        string? lastError,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("messageId", NpgsqlDbType.Uuid, messageId);
        command.Parameters.AddWithValue("leaseToken", NpgsqlDbType.Uuid, leaseToken);
        if (lastError is null)
        {
            command.Parameters.AddWithValue("dispatchedAtUtc", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow);
        }
        else
        {
            command.Parameters.AddWithValue("lastError", NpgsqlDbType.Text, lastError);
        }

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Outbox transition for message '{messageId}' was rejected because the lease no longer belongs to this dispatcher.");
        }
    }
}
