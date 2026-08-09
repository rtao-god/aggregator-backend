using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Aggregator.Ingestion.Worker;

/// <summary>Consumes Catalog configuration activations into the Ingestion-local identity projection.</summary>
public sealed class IngestionCatalogConfigurationProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IngestionCatalogConfigurationProjectionWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IngestionCatalogConfigurationProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public IngestionCatalogConfigurationProjectionWorker(
        IngestionCatalogConfigurationProjectionWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<IngestionCatalogConfigurationProjectionWorker> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new ConnectionFactory
        {
            Uri = _options.BrokerUri,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ClientProvidedName = "ingestion-catalog-configuration-projection-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "ingestion-catalog-configuration-projection-worker",
            stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await DeclareTopologyAsync(_channel, stoppingToken);
        await _channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageAsync;
        _ = await _channel.BasicConsumeAsync(
            queue: _options.Queue,
            autoAck: false,
            consumer: consumer,
            cancellationToken: stoppingToken);
        IngestionCatalogConfigurationProjectionWorkerLog.ConsumerStarted(
            _logger,
            _options.RoutingKey,
            _options.Queue);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    private async Task OnMessageAsync(object sender, BasicDeliverEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(eventArgs);
        var channel = _channel
            ?? throw new InvalidOperationException("Ingestion Catalog projection channel is unavailable.");
        var cancellationToken = eventArgs.CancellationToken;
        try
        {
            ValidateEnvelope(eventArgs);
            var payloadDigest = ReadRequiredHeader(
                eventArgs.BasicProperties.Headers,
                "payload-digest");
            var causationId = ReadOptionalGuidHeader(
                eventArgs.BasicProperties.Headers,
                "causation-id");
            VerifyPayloadIntegrity(eventArgs.Body.Span, payloadDigest);
            var activation = JsonSerializer.Deserialize<CatalogConfigurationActivated>(
                eventArgs.Body.Span,
                SerializerOptions)
                ?? throw new JsonException("Catalog configuration activation payload is empty.");
            ValidateMessageIdentity(activation.EventId, eventArgs.BasicProperties.MessageId);
            var correlationId = ReadRequiredCorrelationId(
                eventArgs.BasicProperties.CorrelationId);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<ApplyCatalogConfigurationActivationService>();
            var result = await service.ApplyAsync(
                activation,
                payloadDigest,
                correlationId,
                cancellationToken);
            await channel.BasicAckAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: cancellationToken);
            IngestionCatalogConfigurationProjectionWorkerLog.ActivationApplied(
                _logger,
                activation.CatalogKey,
                activation.AggregateRevision,
                activation.ConfigurationRevisionId,
                result.Disposition,
                activation.EventId,
                causationId,
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRetryable(exception))
        {
            IngestionCatalogConfigurationProjectionWorkerLog.TransientFailure(
                _logger,
                exception,
                eventArgs.BasicProperties.MessageId);
            await Task.Delay(_options.RetryDelay, cancellationToken);
            await channel.BasicRejectAsync(
                deliveryTag: eventArgs.DeliveryTag,
                requeue: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            IngestionCatalogConfigurationProjectionWorkerLog.MessageDeadLettered(
                _logger,
                exception,
                eventArgs.BasicProperties.MessageId);
            await channel.BasicNackAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken: cancellationToken);
        }
    }

    private async Task DeclareTopologyAsync(
        IChannel channel,
        CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            exchange: _options.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            exchange: _options.DeadLetterExchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: _options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-queue-type"] = "quorum",
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: _options.DeadLetterQueue,
            exchange: _options.DeadLetterExchange,
            routingKey: _options.RoutingKey,
            arguments: null,
            cancellationToken: cancellationToken);
        await channel.QueueDeclareAsync(
            queue: _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = _options.DeliveryLimit,
                ["x-dead-letter-exchange"] = _options.DeadLetterExchange,
                ["x-dead-letter-routing-key"] = _options.RoutingKey,
            },
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: _options.Queue,
            exchange: _options.Exchange,
            routingKey: _options.RoutingKey,
            arguments: null,
            cancellationToken: cancellationToken);
    }

    private void ValidateEnvelope(BasicDeliverEventArgs eventArgs)
    {
        if (!string.Equals(eventArgs.RoutingKey, _options.RoutingKey, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Catalog event routing key '{eventArgs.RoutingKey}' is unsupported.");
        }

        if (!string.Equals(
                eventArgs.BasicProperties.Type,
                CatalogIntegrationEventContracts.ConfigurationActivated,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Catalog event contract '{eventArgs.BasicProperties.Type}' is unsupported.");
        }

        if (!string.Equals(
                eventArgs.BasicProperties.ContentType,
                "application/json",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                eventArgs.BasicProperties.ContentEncoding,
                "utf-8",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException(
                "Catalog configuration activation must use application/json with utf-8 encoding.");
        }
    }

    internal static void VerifyPayloadIntegrity(
        ReadOnlySpan<byte> payload,
        string expectedDigest)
    {
        if (expectedDigest is not { Length: 64 } ||
            expectedDigest.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new JsonException("Catalog configuration payload digest header is invalid.");
        }

        var actualDigest = Convert
            .ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw new JsonException(
                "Catalog configuration payload digest does not match the message body.");
        }
    }

    internal static void ValidateMessageIdentity(Guid eventId, string? messageId)
    {
        if (eventId == Guid.Empty ||
            !Guid.TryParse(messageId, out var parsedMessageId) ||
            parsedMessageId != eventId)
        {
            throw new JsonException(
                "Catalog configuration message ID must match the producer-owned event identity.");
        }
    }

    internal static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is IngestionApplicationException { StatusCode: 503 } ||
               exception is DbException { IsTransient: true } ||
               exception is TimeoutException or IOException ||
               exception.InnerException is not null && IsRetryable(exception.InnerException);
    }

    private static string ReadRequiredCorrelationId(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw new JsonException(
                "RabbitMQ correlation ID is absent or exceeds the Ingestion contract limit.");
        }

        return correlationId.Trim();
    }

    private static Guid? ReadOptionalGuidHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var rawValue) || rawValue is null)
        {
            return null;
        }

        var value = ReadHeaderValue(rawValue, name);
        return Guid.TryParse(value, out var identifier) && identifier != Guid.Empty
            ? identifier
            : throw new JsonException(
                $"RabbitMQ header '{name}' must be absent or contain a non-empty UUID.");
    }

    private static string ReadRequiredHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var rawValue) || rawValue is null)
        {
            throw new JsonException($"Required RabbitMQ header '{name}' is absent.");
        }

        var value = ReadHeaderValue(rawValue, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"RabbitMQ header '{name}' is empty.");
        }

        return value.Trim();
    }

    private static string ReadHeaderValue(object rawValue, string name) =>
        rawValue switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            string text => text,
            _ => throw new JsonException(
                $"RabbitMQ header '{name}' has an unsupported value type."),
        };

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}

internal static partial class IngestionCatalogConfigurationProjectionWorkerLog
{
    [LoggerMessage(
        EventId = 3301,
        Level = LogLevel.Information,
        Message = "Ingestion Catalog configuration consumer is reading {RoutingKey} from {Queue}.")]
    public static partial void ConsumerStarted(
        ILogger logger,
        string routingKey,
        string queue);

    [LoggerMessage(
        EventId = 3302,
        Level = LogLevel.Information,
        Message = "Ingestion applied Catalog configuration activation {AggregateRevision} for {CatalogKey}/{ConfigurationRevisionId}; disposition={Disposition}; event={EventId}; causation={CausationId}; correlation={CorrelationId}.")]
    public static partial void ActivationApplied(
        ILogger logger,
        string catalogKey,
        long aggregateRevision,
        Guid configurationRevisionId,
        CatalogConfigurationProjectionDisposition disposition,
        Guid eventId,
        Guid? causationId,
        string correlationId);

    [LoggerMessage(
        EventId = 3303,
        Level = LogLevel.Warning,
        Message = "Ingestion is requeueing transient Catalog configuration event {MessageId}.")]
    public static partial void TransientFailure(
        ILogger logger,
        Exception exception,
        string? messageId);

    [LoggerMessage(
        EventId = 3304,
        Level = LogLevel.Error,
        Message = "Ingestion dead-lettered invalid or non-transient Catalog configuration event {MessageId}.")]
    public static partial void MessageDeadLettered(
        ILogger logger,
        Exception exception,
        string? messageId);
}
