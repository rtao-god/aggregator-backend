using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Platform.Messaging;

namespace Platform.Messaging.Tests;

public sealed class PostgresOutboxDispatcherIntegrationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactMessageIsClaimedPublishedAndCompleted()
    {
        var scope = await PostgresOutboxScope.TryCreateAsync();
        if (scope is null)
        {
            return;
        }

        await using (scope)
        {
            var message = CreateMessage("{\"state\":\"active\"}");
            await scope.InsertAsync(message);
            var publisher = new RecordingPublisher();
            var dispatcher = CreateDispatcher(scope, publisher, maximumDeliveryAttempts: 3);

            var dispatched = await dispatcher.DispatchOnceAsync(CancellationToken.None);

            Assert.Equal(1, dispatched);
            Assert.Equal(message, Assert.Single(publisher.Messages));
            var state = await scope.ReadStateAsync(message.MessageId);
            Assert.Equal(1, state.DeliveryAttempts);
            Assert.True(state.IsDispatched);
            Assert.False(state.IsDeadLettered);
            Assert.Null(state.LeaseToken);
            Assert.Null(state.LeaseOwner);
            Assert.Null(state.LeaseUntilUtc);
            Assert.Null(state.LastError);
        }
    }

    [Fact]
    public async Task CorruptedPayloadIsDeadLetteredOnFinalAllowedAttempt()
    {
        var scope = await PostgresOutboxScope.TryCreateAsync();
        if (scope is null)
        {
            return;
        }

        await using (scope)
        {
            var valid = CreateMessage("{\"state\":\"active\"}");
            var corrupted = valid with
            {
                PayloadJson = "{\"state\":\"corrupted\"}",
            };
            await scope.InsertAsync(corrupted);
            var dispatcher = CreateDispatcher(
                scope,
                new IntegrityPublisher(),
                maximumDeliveryAttempts: 1);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dispatcher.DispatchOnceAsync(CancellationToken.None));

            Assert.Contains("digest does not match", exception.Message, StringComparison.Ordinal);
            var state = await scope.ReadStateAsync(corrupted.MessageId);
            Assert.Equal(1, state.DeliveryAttempts);
            Assert.False(state.IsDispatched);
            Assert.True(state.IsDeadLettered);
            Assert.Null(state.LeaseToken);
            Assert.Null(state.LeaseOwner);
            Assert.Null(state.LeaseUntilUtc);
            Assert.Contains("digest does not match", state.DeadLetterReason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task StaleDispatcherCannotCompleteAReplacementLease()
    {
        var scope = await PostgresOutboxScope.TryCreateAsync();
        if (scope is null)
        {
            return;
        }

        await using (scope)
        {
            var message = CreateMessage("{\"state\":\"active\"}");
            await scope.InsertAsync(message);
            const string dispatcherIdentity = "messaging-integration-test";
            var replacementLeaseToken = Guid.Parse("0192f5f0-0000-7000-8000-000000000099");
            var publisher = new LeaseReplacingPublisher(
                scope,
                dispatcherIdentity,
                replacementLeaseToken);
            var dispatcher = CreateDispatcher(
                scope,
                publisher,
                maximumDeliveryAttempts: 3,
                dispatcherIdentity);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                dispatcher.DispatchOnceAsync(CancellationToken.None));

            Assert.Contains("lost its exact lease", exception.Message, StringComparison.Ordinal);
            var state = await scope.ReadStateAsync(message.MessageId);
            Assert.Equal(replacementLeaseToken, state.LeaseToken);
            Assert.Equal(dispatcherIdentity, state.LeaseOwner);
            Assert.False(state.IsDispatched);
            Assert.False(state.IsDeadLettered);
        }
    }

    private static PostgresOutboxDispatcher CreateDispatcher(
        PostgresOutboxScope scope,
        IIntegrationEventPublisher publisher,
        int maximumDeliveryAttempts,
        string dispatcherIdentity = "messaging-integration-test") =>
        new(
            new OutboxDispatcherOptions
            {
                ConnectionString = scope.ConnectionString,
                Schema = scope.Schema,
                Table = "outbox_message",
                DispatcherIdentity = dispatcherIdentity,
                BatchSize = 10,
                MaximumDeliveryAttempts = maximumDeliveryAttempts,
                LeaseDuration = TimeSpan.FromMinutes(2),
                EmptyDelay = TimeSpan.FromSeconds(1),
            },
            publisher,
            new FixedTimeProvider(Timestamp));

    private static OutboxMessage CreateMessage(string payload)
    {
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        return new OutboxMessage(
            Guid.Parse("0192f5f0-0000-7000-8000-000000000001"),
            "catalog.publication.activated",
            "aggregator.catalog.publication-activated@1",
            payload,
            digest,
            Timestamp,
            "corr.messaging-integration:0001",
            CausationId: null);
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class RecordingPublisher : IIntegrationEventPublisher
    {
        public List<OutboxMessage> Messages { get; } = [];

        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = OutboxMessageIntegrity.GetVerifiedPayloadBytes(message);
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class IntegrityPublisher : IIntegrationEventPublisher
    {
        public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = OutboxMessageIntegrity.GetVerifiedPayloadBytes(message);
            return Task.CompletedTask;
        }
    }

    private sealed class LeaseReplacingPublisher(
        PostgresOutboxScope scope,
        string dispatcherIdentity,
        Guid replacementLeaseToken) : IIntegrationEventPublisher
    {
        public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            _ = OutboxMessageIntegrity.GetVerifiedPayloadBytes(message);
            await scope.ReplaceLeaseAsync(
                message.MessageId,
                replacementLeaseToken,
                dispatcherIdentity,
                Timestamp.AddMinutes(10),
                cancellationToken);
        }
    }

    private sealed record OutboxRowState(
        int DeliveryAttempts,
        bool IsDispatched,
        bool IsDeadLettered,
        Guid? LeaseToken,
        string? LeaseOwner,
        DateTimeOffset? LeaseUntilUtc,
        string? LastError,
        string? DeadLetterReason);

    private sealed class PostgresOutboxScope : IAsyncDisposable
    {
        private const string EnvironmentVariable = "PLATFORM_MESSAGING_TEST_POSTGRES";

        private PostgresOutboxScope(string connectionString, string schema)
        {
            ConnectionString = connectionString;
            Schema = schema;
        }

        public string ConnectionString { get; }

        public string Schema { get; }

        public static async Task<PostgresOutboxScope?> TryCreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            var schema = $"messaging_test_{Guid.NewGuid():N}";
            var scope = new PostgresOutboxScope(connectionString, schema);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE SCHEMA "{schema}";

                CREATE TABLE "{schema}"."outbox_message"
                (
                    message_id uuid PRIMARY KEY,
                    routing_key varchar(256) NOT NULL,
                    contract_identity varchar(256) NOT NULL,
                    payload_json text NOT NULL,
                    payload_digest char(64) NOT NULL,
                    occurred_at_utc timestamptz NOT NULL,
                    correlation_id varchar(128) NOT NULL,
                    causation_id uuid NULL,
                    lease_token uuid NULL,
                    lease_owner varchar(200) NULL,
                    lease_until_utc timestamptz NULL,
                    delivery_attempts integer NOT NULL DEFAULT 0,
                    dispatched_at_utc timestamptz NULL,
                    last_error varchar(4000) NULL,
                    dead_lettered_at_utc timestamptz NULL,
                    dead_letter_reason varchar(4000) NULL
                );
                """;
            await command.ExecuteNonQueryAsync();
            return scope;
        }

        public async Task InsertAsync(OutboxMessage message)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                INSERT INTO "{Schema}"."outbox_message"
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
                    @messageId,
                    @routingKey,
                    @contractIdentity,
                    @payloadJson,
                    @payloadDigest,
                    @occurredAtUtc,
                    @correlationId,
                    @causationId
                );
                """;
            command.Parameters.AddWithValue("messageId", NpgsqlDbType.Uuid, message.MessageId);
            command.Parameters.AddWithValue("routingKey", message.RoutingKey);
            command.Parameters.AddWithValue("contractIdentity", message.ContractIdentity);
            command.Parameters.AddWithValue("payloadJson", message.PayloadJson);
            command.Parameters.AddWithValue("payloadDigest", message.PayloadDigest);
            command.Parameters.AddWithValue(
                "occurredAtUtc",
                NpgsqlDbType.TimestampTz,
                message.OccurredAtUtc);
            command.Parameters.AddWithValue("correlationId", message.CorrelationId);
            command.Parameters.AddWithValue(
                "causationId",
                NpgsqlDbType.Uuid,
                message.CausationId is null ? DBNull.Value : message.CausationId.Value);
            await command.ExecuteNonQueryAsync();
        }

        public async Task ReplaceLeaseAsync(
            Guid messageId,
            Guid leaseToken,
            string leaseOwner,
            DateTimeOffset leaseUntilUtc,
            CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                UPDATE "{Schema}"."outbox_message"
                SET lease_token = @leaseToken,
                    lease_owner = @leaseOwner,
                    lease_until_utc = @leaseUntilUtc
                WHERE message_id = @messageId;
                """;
            command.Parameters.AddWithValue("messageId", NpgsqlDbType.Uuid, messageId);
            command.Parameters.AddWithValue("leaseToken", NpgsqlDbType.Uuid, leaseToken);
            command.Parameters.AddWithValue("leaseOwner", leaseOwner);
            command.Parameters.AddWithValue(
                "leaseUntilUtc",
                NpgsqlDbType.TimestampTz,
                leaseUntilUtc);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(cancellationToken));
        }

        public async Task<OutboxRowState> ReadStateAsync(Guid messageId)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT delivery_attempts,
                       dispatched_at_utc IS NOT NULL,
                       dead_lettered_at_utc IS NOT NULL,
                       lease_token,
                       lease_owner,
                       lease_until_utc,
                       last_error,
                       dead_letter_reason
                FROM "{Schema}"."outbox_message"
                WHERE message_id = @messageId;
                """;
            command.Parameters.AddWithValue("messageId", NpgsqlDbType.Uuid, messageId);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            return new OutboxRowState(
                reader.GetInt32(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7));
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP SCHEMA IF EXISTS \"{Schema}\" CASCADE;";
            await command.ExecuteNonQueryAsync();
        }
    }
}
