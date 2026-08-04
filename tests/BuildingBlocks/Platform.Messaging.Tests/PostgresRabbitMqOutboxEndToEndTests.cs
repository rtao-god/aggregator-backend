using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;
using Platform.Messaging;
using RabbitMQ.Client;

namespace Platform.Messaging.Tests;

public sealed class PostgresRabbitMqOutboxEndToEndTests
{
    private const string PostgresEnvironmentVariable = "PLATFORM_MESSAGING_TEST_POSTGRES";
    private const string RabbitMqEnvironmentVariable = "PLATFORM_MESSAGING_TEST_RABBITMQ";

    [Fact]
    public async Task DurableOutboxDeliveryReachesRabbitMqAndPersistsCompletion()
    {
        var postgresConnectionString = Environment.GetEnvironmentVariable(PostgresEnvironmentVariable);
        var brokerUriValue = Environment.GetEnvironmentVariable(RabbitMqEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(postgresConnectionString) ||
            string.IsNullOrWhiteSpace(brokerUriValue))
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("N");
        var schema = $"messaging_e2e_{suffix}";
        var exchange = $"platform.messaging.e2e.{suffix}";
        var brokerUri = new Uri(brokerUriValue, UriKind.Absolute);
        var message = CreateMessage();

        await CreateOutboxAsync(postgresConnectionString, schema, message);
        try
        {
            var setupClientName = $"platform-messaging-e2e-setup-{suffix}";
            var connectionFactory = new ConnectionFactory
            {
                Uri = brokerUri,
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                ClientProvidedName = setupClientName,
            };
            await using var connection = await connectionFactory.CreateConnectionAsync(
                setupClientName,
                CancellationToken.None);
            await using var channel = await connection.CreateChannelAsync(
                null,
                CancellationToken.None);
            await channel.ExchangeDeclareAsync(
                exchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                noWait: false,
                CancellationToken.None);
            var declaredQueue = await channel.QueueDeclareAsync(
                cancellationToken: CancellationToken.None);
            await channel.QueueBindAsync(
                declaredQueue.QueueName,
                exchange,
                message.RoutingKey,
                arguments: null,
                noWait: false,
                CancellationToken.None);

            await using var publisher = new RabbitMqEventPublisher(new RabbitMqPublisherOptions
            {
                BrokerUri = brokerUri,
                Exchange = exchange,
                ClientProvidedName = $"platform-messaging-e2e-publisher-{suffix}",
            });
            var dispatcher = new PostgresOutboxDispatcher(
                new OutboxDispatcherOptions
                {
                    ConnectionString = postgresConnectionString,
                    Schema = schema,
                    DispatcherIdentity = $"platform-messaging-e2e-dispatcher-{suffix}",
                    BatchSize = 10,
                    MaximumDeliveryAttempts = 3,
                    LeaseDuration = TimeSpan.FromMinutes(2),
                    EmptyDelay = TimeSpan.FromSeconds(1),
                },
                publisher,
                new FixedTimeProvider(message.OccurredAtUtc));

            var dispatched = await dispatcher.DispatchOnceAsync(CancellationToken.None);
            var delivery = await GetEventuallyAsync(channel, declaredQueue.QueueName);

            Assert.Equal(1, dispatched);
            Assert.Equal(message.PayloadJson, Encoding.UTF8.GetString(delivery.Body.Span));
            Assert.Equal(message.MessageId.ToString("D"), delivery.BasicProperties.MessageId);
            Assert.True(await IsCompletedAsync(
                postgresConnectionString,
                schema,
                message.MessageId));

            await channel.ExchangeDeleteAsync(
                exchange,
                ifUnused: false,
                noWait: false,
                CancellationToken.None);
        }
        finally
        {
            await DropSchemaAsync(postgresConnectionString, schema);
        }
    }

    private static async Task CreateOutboxAsync(
        string connectionString,
        string schema,
        OutboxMessage message)
    {
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
                leased_by varchar(200) NULL,
                lease_expires_at_utc timestamptz NULL,
                delivery_attempts integer NOT NULL DEFAULT 0,
                dispatched_at_utc timestamptz NULL,
                last_error varchar(4000) NULL,
                dead_lettered_at_utc timestamptz NULL,
                dead_letter_reason varchar(4000) NULL
            );

            INSERT INTO "{schema}"."outbox_message"
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

    private static async Task<bool> IsCompletedAsync(
        string connectionString,
        string schema,
        Guid messageId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT dispatched_at_utc IS NOT NULL
               AND dead_lettered_at_utc IS NULL
               AND lease_token IS NULL
               AND leased_by IS NULL
               AND lease_expires_at_utc IS NULL
            FROM "{schema}"."outbox_message"
            WHERE message_id = @messageId;
            """;
        command.Parameters.AddWithValue("messageId", NpgsqlDbType.Uuid, messageId);
        return (bool)(await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Outbox message completion state was not found."));
    }

    private static async Task DropSchemaAsync(string connectionString, string schema)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<BasicGetResult> GetEventuallyAsync(IChannel channel, string queueName)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var delivery = await channel.BasicGetAsync(
                queueName,
                autoAck: true,
                CancellationToken.None);
            if (delivery is not null)
            {
                return delivery;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new InvalidOperationException("RabbitMQ did not expose the outbox delivery.");
    }

    private static OutboxMessage CreateMessage()
    {
        const string payload = "{\"eventId\":\"0192f5f0-0000-7000-8000-000000000011\",\"state\":\"active\"}";
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        return new OutboxMessage(
            Guid.Parse("0192f5f0-0000-7000-8000-000000000011"),
            "catalog.publication.activated",
            "aggregator.catalog.publication-activated@1",
            payload,
            digest,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "corr.messaging-e2e:0001",
            Guid.Parse("0192f5f0-0000-7000-8000-000000000012"));
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }
}
