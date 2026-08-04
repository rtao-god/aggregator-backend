using RabbitMQ.Client;

namespace Platform.Messaging;

public sealed record RabbitMqPublisherOptions
{
    public required Uri BrokerUri { get; init; }

    public required string Exchange { get; init; }

    public required string ClientProvidedName { get; init; }

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BrokerUri);
        if (BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new ArgumentException("RabbitMQ URI must use amqp or amqps.", nameof(BrokerUri));
        }

        if (string.IsNullOrWhiteSpace(Exchange))
        {
            throw new ArgumentException("Exchange is required.", nameof(Exchange));
        }

        if (string.IsNullOrWhiteSpace(ClientProvidedName))
        {
            throw new ArgumentException("ClientProvidedName is required.", nameof(ClientProvidedName));
        }
    }
}

/// <summary>Publishes confirmed messages to the platform topic exchange.</summary>
public sealed class RabbitMqEventPublisher : IIntegrationEventPublisher, IAsyncDisposable
{
    private readonly RabbitMqPublisherOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public RabbitMqEventPublisher(RabbitMqPublisherOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = OutboxMessageIntegrity.GetVerifiedPayloadBytes(message);
        var channel = await GetChannelAsync(cancellationToken);
        var properties = new BasicProperties
        {
            AppId = _options.ClientProvidedName,
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = message.MessageId.ToString("D"),
            CorrelationId = message.CorrelationId,
            Type = message.ContractIdentity,
            Timestamp = new AmqpTimestamp(message.OccurredAtUtc.ToUnixTimeSeconds()),
            Headers = new Dictionary<string, object?>
            {
                ["payload-digest"] = message.PayloadDigest,
                ["causation-id"] = message.CausationId?.ToString("D"),
            },
        };

        await channel.BasicPublishAsync(
            _options.Exchange,
            message.RoutingKey,
            mandatory: true,
            properties,
            body,
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
        }

        _gate.Dispose();
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (_channel is not null)
            {
                await _channel.DisposeAsync();
                _channel = null;
            }

            if (_connection is null || !_connection.IsOpen)
            {
                if (_connection is not null)
                {
                    await _connection.DisposeAsync();
                }

                var factory = new ConnectionFactory
                {
                    Uri = _options.BrokerUri,
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                    ClientProvidedName = _options.ClientProvidedName,
                    RequestedHeartbeat = TimeSpan.FromSeconds(30),
                };
                _connection = await factory.CreateConnectionAsync(_options.ClientProvidedName, cancellationToken);
            }

            _channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true),
                cancellationToken);
            await _channel.ExchangeDeclareAsync(
                _options.Exchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                noWait: false,
                cancellationToken);
            return _channel;
        }
        finally
        {
            _gate.Release();
        }
    }
}
