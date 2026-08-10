using Npgsql;
using NpgsqlTypes;

namespace Platform.Messaging;

/// <summary>Leases durable messages in a short transaction, publishes outside it, and records exact delivery.</summary>
public sealed class PostgresOutboxDispatcher
{
    private readonly OutboxDispatcherOptions _options;
    private readonly IIntegrationEventPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly string _qualifiedTable;

    public PostgresOutboxDispatcher(
        OutboxDispatcherOptions options,
        IIntegrationEventPublisher publisher,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _qualifiedTable = $"\"{_options.Schema}\".\"outbox_message\"";
    }

    public async Task<int> DispatchOnceAsync(CancellationToken cancellationToken)
    {
        var leaseToken = Guid.CreateVersion7();
        var messages = await LeaseAsync(leaseToken, cancellationToken);
        foreach (var message in messages)
        {
            try
            {
                _ = OutboxMessageIntegrity.GetVerifiedPayloadBytes(message);
                await _publisher.PublishAsync(message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var deadLettered = await MarkFailedAsync(
                    message.MessageId,
                    leaseToken,
                    exception,
                    _timeProvider.GetUtcNow(),
                    cancellationToken);
                throw new OutboxDispatchAttemptException(
                    message.MessageId,
                    deadLettered,
                    exception);
            }

            await MarkDispatchedAsync(
                message.MessageId,
                leaseToken,
                _timeProvider.GetUtcNow(),
                cancellationToken);
        }

        return messages.Count;
    }

    private async Task<IReadOnlyList<OutboxMessage>> LeaseAsync(
        Guid leaseToken,
        CancellationToken cancellationToken)
    {
        var leasedAtUtc = _timeProvider.GetUtcNow();
        var result = new List<OutboxMessage>(_options.BatchSize);
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var exhaustedCommand = connection.CreateCommand())
        {
            exhaustedCommand.Transaction = transaction;
            exhaustedCommand.CommandText = $"""
                UPDATE {_qualifiedTable}
                SET lease_token = NULL,
                    leased_by = NULL,
                    lease_expires_at_utc = NULL,
                    dead_lettered_at_utc = @deadLetteredAtUtc,
                    dead_letter_reason = COALESCE(
                        last_error,
                        'Delivery attempt budget was exhausted before the message reached the broker.')
                WHERE dispatched_at_utc IS NULL
                  AND dead_lettered_at_utc IS NULL
                  AND delivery_attempts >= @maximumDeliveryAttempts
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= @deadLetteredAtUtc);
                """;
            exhaustedCommand.Parameters.AddWithValue(
                "deadLetteredAtUtc",
                NpgsqlDbType.TimestampTz,
                leasedAtUtc);
            exhaustedCommand.Parameters.AddWithValue(
                "maximumDeliveryAttempts",
                NpgsqlDbType.Integer,
                _options.MaximumDeliveryAttempts);
            await exhaustedCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = $"""
                WITH claim AS
                (
                    SELECT message_id
                    FROM {_qualifiedTable}
                    WHERE dispatched_at_utc IS NULL
                      AND dead_lettered_at_utc IS NULL
                      AND delivery_attempts < @maximumDeliveryAttempts
                      AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= @leasedAtUtc)
                    ORDER BY occurred_at_utc, message_id
                    FOR UPDATE SKIP LOCKED
                    LIMIT @batchSize
                )
                UPDATE {_qualifiedTable} AS message
                SET lease_token = @leaseToken,
                    leased_by = @leasedBy,
                    lease_expires_at_utc = @leaseExpiresAtUtc,
                    delivery_attempts = message.delivery_attempts + 1,
                    last_error = NULL
                FROM claim
                WHERE message.message_id = claim.message_id
                RETURNING message.message_id,
                          message.routing_key,
                          message.contract_identity,
                          message.payload_json,
                          message.payload_digest,
                          message.occurred_at_utc,
                          message.correlation_id,
                          message.causation_id;
                """;
            command.Parameters.AddWithValue(
                "maximumDeliveryAttempts",
                NpgsqlDbType.Integer,
                _options.MaximumDeliveryAttempts);
            command.Parameters.AddWithValue("leasedAtUtc", NpgsqlDbType.TimestampTz, leasedAtUtc);
            command.Parameters.AddWithValue("batchSize", NpgsqlDbType.Integer, _options.BatchSize);
            command.Parameters.AddWithValue("leaseToken", NpgsqlDbType.Uuid, leaseToken);
            command.Parameters.AddWithValue("leasedBy", NpgsqlDbType.Text, _options.DispatcherIdentity);
            command.Parameters.AddWithValue(
                "leaseExpiresAtUtc",
                NpgsqlDbType.TimestampTz,
                leasedAtUtc + _options.LeaseDuration);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
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

    private async Task MarkDispatchedAsync(
        Guid messageId,
        Guid leaseToken,
        DateTimeOffset dispatchedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {_qualifiedTable}
            SET dispatched_at_utc = @dispatchedAtUtc,
                lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL,
                last_error = NULL
            WHERE message_id = @messageId
              AND lease_token = @leaseToken
              AND dispatched_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL;
            """;
        command.Parameters.AddWithValue("messageId", NpgsqlDbType.Uuid, messageId);
        command.Parameters.AddWithValue("leaseToken", NpgsqlDbType.Uuid, leaseToken);
        command.Parameters.AddWithValue("dispatchedAtUtc", NpgsqlDbType.TimestampTz, dispatchedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new OutboxLeaseLostException(
                messageId,
                leaseToken,
                _options.DispatcherIdentity);
        }
    }

    private async Task<bool> MarkFailedAsync(
        Guid messageId,
        Guid leaseToken,
        Exception exception,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        var error = $"{exception.GetType().Name}: {exception.Message}";
        if (error.Length > 2000)
        {
            error = error[..2000];
        }

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            UPDATE {_qualifiedTable}
            SET lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL,
                last_error = @lastError,
                dead_lettered_at_utc = CASE
                    WHEN delivery_attempts >= @maximumDeliveryAttempts THEN @failedAtUtc
                    ELSE dead_lettered_at_utc
                END,
                dead_letter_reason = CASE
                    WHEN delivery_attempts >= @maximumDeliveryAttempts THEN @lastError
                    ELSE dead_letter_reason
                END
            WHERE message_id = @messageId
              AND lease_token = @leaseToken
              AND dispatched_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL
            RETURNING dead_lettered_at_utc IS NOT NULL;
            """;
        command.Parameters.AddWithValue("messageId", NpgsqlDbType.Uuid, messageId);
        command.Parameters.AddWithValue("leaseToken", NpgsqlDbType.Uuid, leaseToken);
        command.Parameters.AddWithValue("lastError", NpgsqlDbType.Text, error);
        command.Parameters.AddWithValue(
            "maximumDeliveryAttempts",
            NpgsqlDbType.Integer,
            _options.MaximumDeliveryAttempts);
        command.Parameters.AddWithValue("failedAtUtc", NpgsqlDbType.TimestampTz, failedAtUtc);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is not bool deadLettered)
        {
            throw new OutboxLeaseLostException(
                messageId,
                leaseToken,
                _options.DispatcherIdentity,
                exception);
        }

        return deadLettered;
    }
}
