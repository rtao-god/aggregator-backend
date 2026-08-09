using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Analytics.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Aggregator.Analytics.Worker;

/// <summary>Consumes Catalog listing access changes into the Analytics-local authorization projection.</summary>
public sealed class AnalyticsListingAccessProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly AnalyticsListingAccessProjectionWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalyticsListingAccessProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public AnalyticsListingAccessProjectionWorker(
        AnalyticsListingAccessProjectionWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<AnalyticsListingAccessProjectionWorker> logger)
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
            ClientProvidedName = "analytics-listing-access-projection-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "analytics-listing-access-projection-worker",
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
        AnalyticsListingAccessProjectionWorkerLog.ConsumerStarted(
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
            ?? throw new InvalidOperationException("Analytics access worker channel is unavailable.");
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
            AnalyticsPublicReadProjectionWorker.VerifyPayloadIntegrity(
                eventArgs.Body.Span,
                payloadDigest);
            var accessEvent = JsonSerializer.Deserialize<CatalogListingAccessGrantChanged>(
                eventArgs.Body.Span,
                SerializerOptions)
                ?? throw new JsonException("Catalog listing access payload is empty.");
            AnalyticsPublicReadProjectionWorker.ValidateMessageIdentity(
                accessEvent.EventId,
                eventArgs.BasicProperties.MessageId);
            var correlationId = ReadRequiredCorrelationId(
                eventArgs.BasicProperties.CorrelationId);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<ApplyCatalogListingAccessGrantChangedService>();
            var result = await service.ApplyAsync(
                new CatalogListingAccessGrantProjectionMessage(
                    accessEvent.EventId,
                    eventArgs.RoutingKey,
                    eventArgs.BasicProperties.Type!,
                    payloadDigest,
                    correlationId,
                    causationId,
                    accessEvent),
                cancellationToken);
            await channel.BasicAckAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: cancellationToken);
            AnalyticsListingAccessProjectionWorkerLog.AccessApplied(
                _logger,
                accessEvent.GrantId,
                accessEvent.ListingId,
                accessEvent.ActorId,
                accessEvent.AggregateRevision,
                result.Disposition,
                accessEvent.EventId,
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            AnalyticsPublicReadProjectionWorker.IsRetryable(exception))
        {
            AnalyticsListingAccessProjectionWorkerLog.TransientFailure(
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
            AnalyticsListingAccessProjectionWorkerLog.MessageDeadLettered(
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
                CatalogIntegrationEventContracts.ListingAccessGrantChanged,
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
                "Catalog listing access event must use application/json with utf-8 encoding.");
        }
    }

    private static string ReadRequiredCorrelationId(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw new JsonException(
                "RabbitMQ correlation ID is absent or exceeds the Analytics contract limit.");
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
                $"RabbitMQ header '{name}' must contain a non-empty UUID when present.");
    }

    private static string ReadRequiredHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var rawValue) || rawValue is null)
        {
            throw new JsonException($"Required RabbitMQ header '{name}' is absent.");
        }

        return ReadHeaderValue(rawValue, name);
    }

    private static string ReadHeaderValue(object rawValue, string name)
    {
        var value = rawValue switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            string text => text,
            _ => throw new JsonException(
                $"RabbitMQ header '{name}' has an unsupported value type."),
        };
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"RabbitMQ header '{name}' is empty.");
        }

        return value.Trim();
    }

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

internal static partial class AnalyticsListingAccessProjectionWorkerLog
{
    [LoggerMessage(
        EventId = 2201,
        Level = LogLevel.Information,
        Message = "Analytics listing-access consumer is reading {RoutingKey} from {Queue}.")]
    public static partial void ConsumerStarted(
        ILogger logger,
        string routingKey,
        string queue);

    [LoggerMessage(
        EventId = 2202,
        Level = LogLevel.Information,
        Message = "Analytics applied Catalog grant {GrantId} for listing {ListingId} and actor {ActorId}; revision={Revision}; disposition={Disposition}; event={EventId}; correlation={CorrelationId}.")]
    public static partial void AccessApplied(
        ILogger logger,
        Guid grantId,
        Guid listingId,
        Guid actorId,
        long revision,
        ListingMetricsAccessProjectionDisposition disposition,
        Guid eventId,
        string correlationId);

    [LoggerMessage(
        EventId = 2203,
        Level = LogLevel.Warning,
        Message = "Analytics is requeueing transient Catalog listing-access event {MessageId}.")]
    public static partial void TransientFailure(
        ILogger logger,
        Exception exception,
        string? messageId);

    [LoggerMessage(
        EventId = 2204,
        Level = LogLevel.Error,
        Message = "Analytics dead-lettered invalid or non-transient Catalog listing-access event {MessageId}.")]
    public static partial void MessageDeadLettered(
        ILogger logger,
        Exception exception,
        string? messageId);
}
