using System.Security.Cryptography;
using System.Text;
using Platform.Messaging;
using RabbitMQ.Client;

namespace Platform.Messaging.Tests;

public sealed class RabbitMqEventPublisherIntegrationTests
{
    private const string EnvironmentVariable = "PLATFORM_MESSAGING_TEST_RABBITMQ";

    [Fact]
    public async Task ConfirmedPublishPreservesExactProducerEnvelope()
    {
        var brokerUriValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(brokerUriValue))
        {
            return;
        }

        var brokerUri = new Uri(brokerUriValue, UriKind.Absolute);
        var suffix = Guid.NewGuid().ToString("N");
        var exchange = $"platform.messaging.test.{suffix}";
        var queueName = $"platform.messaging.test.{suffix}";
        const string routingKey = "catalog.publication.activated";
        var message = CreateMessage();
        var factory = new ConnectionFactory
        {
            Uri = brokerUri,
            AutomaticRecoveryEnabled = false,
            TopologyRecoveryEnabled = false,
            ClientProvidedName = $"platform-messaging-test-setup-{suffix}",
        };

        await using var connection = await factory.CreateConnectionAsync(
            factory.ClientProvidedName,
            CancellationToken.None);
        await using var channel = await connection.CreateChannelAsync(
            options: null,
            CancellationToken.None);
        await channel.ExchangeDeclareAsync(
            exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            CancellationToken.None);
        await channel.QueueDeclareAsync(
            queueName,
            durable: false,
            exclusive: true,
            autoDelete: true,
            arguments: null,
            passive: false,
            noWait: false,
            CancellationToken.None);
        await channel.QueueBindAsync(
            queueName,
            exchange,
            routingKey,
            arguments: null,
            noWait: false,
            CancellationToken.None);

        await using (var publisher = new RabbitMqEventPublisher(new RabbitMqPublisherOptions
        {
            BrokerUri = brokerUri,
            Exchange = exchange,
            ClientProvidedName = $"platform-messaging-test-publisher-{suffix}",
        }))
        {
            await publisher.PublishAsync(message, CancellationToken.None);
        }

        var delivery = await GetEventuallyAsync(channel, queueName);
        Assert.NotNull(delivery);
        Assert.Equal(exchange, delivery.Exchange);
        Assert.Equal(routingKey, delivery.RoutingKey);
        Assert.Equal(message.PayloadJson, Encoding.UTF8.GetString(delivery.Body.Span));
        Assert.Equal(message.MessageId.ToString("D"), delivery.BasicProperties.MessageId);
        Assert.Equal(message.CorrelationId, delivery.BasicProperties.CorrelationId);
        Assert.Equal(message.ContractIdentity, delivery.BasicProperties.Type);
        Assert.Equal("application/json", delivery.BasicProperties.ContentType);
        Assert.Equal("utf-8", delivery.BasicProperties.ContentEncoding);
        Assert.Equal(DeliveryModes.Persistent, delivery.BasicProperties.DeliveryMode);
        Assert.Equal(
            message.OccurredAtUtc.ToUnixTimeSeconds(),
            delivery.BasicProperties.Timestamp.UnixTime);
        Assert.Equal(
            message.PayloadDigest,
            ReadHeaderText(delivery.BasicProperties.Headers, "payload-digest"));
        Assert.Equal(
            message.CausationId?.ToString("D"),
            ReadHeaderText(delivery.BasicProperties.Headers, "causation-id"));

        await channel.ExchangeDeleteAsync(
            exchange,
            ifUnused: false,
            noWait: false,
            CancellationToken.None);
    }

    [Fact]
    public async Task CorruptedPayloadIsRejectedBeforeBrokerConnection()
    {
        var validMessage = CreateMessage();
        var corruptedMessage = validMessage with { PayloadJson = "{}" };
        await using var publisher = new RabbitMqEventPublisher(new RabbitMqPublisherOptions
        {
            BrokerUri = new Uri("amqp://127.0.0.1:1", UriKind.Absolute),
            Exchange = "platform.messaging.integrity-test",
            ClientProvidedName = "platform-messaging-integrity-test",
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            publisher.PublishAsync(corruptedMessage, CancellationToken.None));

        Assert.Contains("digest does not match", exception.Message, StringComparison.Ordinal);
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

        throw new InvalidOperationException("RabbitMQ did not expose the confirmed test delivery.");
    }

    private static string? ReadHeaderText(
        IDictionary<string, object?>? headers,
        string key)
    {
        if (headers is null || !headers.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            string text => text,
            _ => value.ToString(),
        };
    }

    private static OutboxMessage CreateMessage()
    {
        const string payload = "{\"eventId\":\"0192f5f0-0000-7000-8000-000000000001\",\"state\":\"active\"}";
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        return new OutboxMessage(
            Guid.Parse("0192f5f0-0000-7000-8000-000000000001"),
            "catalog.publication.activated",
            "aggregator.catalog.publication-activated@1",
            payload,
            digest,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "corr.rabbitmq-integration:0001",
            Guid.Parse("0192f5f0-0000-7000-8000-000000000002"));
    }
}
