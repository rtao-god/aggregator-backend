using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Promotion.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Aggregator.Promotion.Worker;

/// <summary>Consumes Catalog listing eligibility events into the Promotion-local fail-closed projection.</summary>
public sealed class PromotionEligibilityProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly PromotionEligibilityProjectionWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PromotionEligibilityProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public PromotionEligibilityProjectionWorker(
        PromotionEligibilityProjectionWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<PromotionEligibilityProjectionWorker> logger)
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
            ClientProvidedName = "promotion-catalog-eligibility-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "promotion-catalog-eligibility-worker",
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
        PromotionEligibilityProjectionWorkerLog.ConsumerStarted(
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
            ?? throw new InvalidOperationException(
                "Promotion eligibility consumer channel is unavailable.");
        var cancellationToken = eventArgs.CancellationToken;
        try
        {
            ValidateEnvelope(eventArgs);
            var payloadDigest = ReadRequiredHeader(
                eventArgs.BasicProperties.Headers,
                "payload-digest");
            VerifyPayloadIntegrity(eventArgs.Body.Span, payloadDigest);
            var integrationEvent = JsonSerializer.Deserialize<
                    CatalogListingPromotionEligibilityChanged>(
                    eventArgs.Body.Span,
                    SerializerOptions)
                ?? throw new JsonException(
                    "Catalog listing eligibility payload is empty.");
            var messageId = ValidateMessageIdentity(
                integrationEvent.EventId,
                eventArgs.BasicProperties.MessageId);
            var correlationId = ReadRequiredCorrelationId(
                eventArgs.BasicProperties.CorrelationId);
            var causationId = ReadOptionalGuidHeader(
                eventArgs.BasicProperties.Headers,
                "causation-id");

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<ApplyCatalogListingPromotionEligibilityService>();
            var result = await service.ApplyAsync(
                new PromotionEligibilityProjectionMessage(
                    messageId,
                    eventArgs.BasicProperties.Type!,
                    payloadDigest,
                    correlationId,
                    causationId,
                    integrationEvent),
                cancellationToken);
            await channel.BasicAckAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: cancellationToken);
            PromotionEligibilityProjectionWorkerLog.EligibilityApplied(
                _logger,
                integrationEvent.CatalogKey,
                integrationEvent.ListingId,
                integrationEvent.EligibilityRevision,
                result,
                messageId,
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRetryable(exception))
        {
            PromotionEligibilityProjectionWorkerLog.TransientFailure(
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
            PromotionEligibilityProjectionWorkerLog.MessageDeadLettered(
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
                $"Catalog event routing key '{eventArgs.RoutingKey}' is unsupported by Promotion.");
        }

        if (!string.Equals(
                eventArgs.BasicProperties.Type,
                CatalogIntegrationEventContracts.ListingPromotionEligibilityChanged,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Catalog event contract '{eventArgs.BasicProperties.Type}' is unsupported by Promotion.");
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
                "Catalog listing eligibility events must use application/json with utf-8 encoding.");
        }
    }

    internal static void VerifyPayloadIntegrity(
        ReadOnlySpan<byte> payload,
        string expectedDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest) ||
            expectedDigest.Length != 64 ||
            expectedDigest.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new JsonException(
                "Catalog listing eligibility payload digest header is invalid.");
        }

        var actualDigest = Convert
            .ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw new JsonException(
                "Catalog listing eligibility payload digest does not match the message body.");
        }
    }

    internal static Guid ValidateMessageIdentity(Guid eventId, string? messageId)
    {
        if (eventId == Guid.Empty ||
            !Guid.TryParse(messageId, out var parsedMessageId) ||
            parsedMessageId != eventId)
        {
            throw new JsonException(
                "Catalog listing eligibility message ID must match the producer-owned event identity.");
        }

        return parsedMessageId;
    }

    internal static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is PromotionApplicationException { StatusCode: 503 } ||
               exception is DbException { IsTransient: true } ||
               exception is TimeoutException or IOException ||
               exception.InnerException is not null && IsRetryable(exception.InnerException);
    }

    internal static Guid? ReadOptionalGuidHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var rawValue) || rawValue is null)
        {
            return null;
        }

        var value = ReadHeaderValue(rawValue, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value, out var identifier) && identifier != Guid.Empty
            ? identifier
            : throw new JsonException(
                $"RabbitMQ header '{name}' must contain an absent value or a non-empty UUID.");
    }

    private static string ReadRequiredCorrelationId(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) ||
            correlationId.Length > 128 ||
            correlationId.Any(char.IsControl))
        {
            throw new JsonException(
                "RabbitMQ correlation ID is absent or invalid for the Promotion contract.");
        }

        return correlationId.Trim();
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

internal static partial class PromotionEligibilityProjectionWorkerLog
{
    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Information,
        Message = "Promotion eligibility consumer is reading {RoutingKey} from {Queue}.")]
    public static partial void ConsumerStarted(
        ILogger logger,
        string routingKey,
        string queue);

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Information,
        Message = "Promotion applied Catalog eligibility revision {EligibilityRevision} for {CatalogKey}/{ListingId}; disposition={Disposition}; event={EventId}; correlation={CorrelationId}.")]
    public static partial void EligibilityApplied(
        ILogger logger,
        string catalogKey,
        Guid listingId,
        long eligibilityRevision,
        PromotionEligibilityProjectionApplyResult disposition,
        Guid eventId,
        string correlationId);

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Warning,
        Message = "Promotion is requeueing transient Catalog eligibility event {MessageId}.")]
    public static partial void TransientFailure(
        ILogger logger,
        Exception exception,
        string? messageId);

    [LoggerMessage(
        EventId = 4104,
        Level = LogLevel.Error,
        Message = "Promotion dead-lettered invalid or non-transient Catalog eligibility event {MessageId}.")]
    public static partial void MessageDeadLettered(
        ILogger logger,
        Exception exception,
        string? messageId);
}
