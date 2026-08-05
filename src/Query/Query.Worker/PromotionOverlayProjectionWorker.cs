using System.Data.Common;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Promotion.Contracts;
using Aggregator.Query.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Aggregator.Query.Worker;

public sealed class PromotionOverlayProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly QueryPromotionWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PromotionOverlayProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public PromotionOverlayProjectionWorker(
        QueryPromotionWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<PromotionOverlayProjectionWorker> logger)
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
            ClientProvidedName = "query-promotion-placement-projection-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "query-promotion-placement-projection-worker",
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
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Query Promotion projection worker is consuming {RoutingKey} from {Queue}",
                _options.RoutingKey,
                _options.Queue);
        }

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
            ?? throw new InvalidOperationException("Query Promotion worker channel is unavailable.");
        var cancellationToken = eventArgs.CancellationToken;
        try
        {
            if (!string.Equals(
                    eventArgs.BasicProperties.Type,
                    PromotionIntegrationEventContracts.PlacementChanged,
                    StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Promotion message contract '{eventArgs.BasicProperties.Type}' is unsupported.");
            }

            var payloadDigest = ReadRequiredHeader(eventArgs.BasicProperties.Headers, "payload-digest");
            VerifyPayloadIntegrity(eventArgs.Body.Span, payloadDigest);
            var change = JsonSerializer.Deserialize<SponsoredPlacementChanged>(
                eventArgs.Body.Span,
                SerializerOptions)
                ?? throw new JsonException("Promotion placement change payload is empty.");
            ValidateMessageIdentity(change.EventId, eventArgs.BasicProperties.MessageId);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<PromotionOverlayProjectionService>();
            var result = await service.ApplyAsync(change, payloadDigest, cancellationToken);
            await channel.BasicAckAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: cancellationToken);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Applied Promotion placement {PlacementId} to public read revision {PublicReadRevisionId}; disposition={Disposition}",
                    change.PlacementId,
                    result.PublicReadRevision.Id,
                    result.Disposition);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRetryableProjectionFailure(exception))
        {
            _logger.LogWarning(
                exception,
                "Requeueing transient Promotion placement message {MessageId}",
                eventArgs.BasicProperties.MessageId);
            await Task.Delay(_options.RetryDelay, cancellationToken);
            await channel.BasicRejectAsync(
                deliveryTag: eventArgs.DeliveryTag,
                requeue: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is QueryProjectionException or JsonException or ArgumentException)
        {
            _logger.LogError(
                exception,
                "Dead-lettering invalid Promotion placement message {MessageId}",
                eventArgs.BasicProperties.MessageId);
            await channel.BasicNackAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Dead-lettering non-transient Promotion placement message {MessageId}",
                eventArgs.BasicProperties.MessageId);
            await channel.BasicNackAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken: cancellationToken);
        }
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
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
        var deadLetterQueueArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-queue-type"] = "quorum",
        };
        await channel.QueueDeclareAsync(
            queue: _options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: deadLetterQueueArguments,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: _options.DeadLetterQueue,
            exchange: _options.DeadLetterExchange,
            routingKey: _options.RoutingKey,
            arguments: null,
            cancellationToken: cancellationToken);
        var queueArguments = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["x-queue-type"] = "quorum",
            ["x-delivery-limit"] = _options.DeliveryLimit,
            ["x-dead-letter-exchange"] = _options.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = _options.RoutingKey,
        };
        await channel.QueueDeclareAsync(
            queue: _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            queue: _options.Queue,
            exchange: _options.Exchange,
            routingKey: _options.RoutingKey,
            arguments: null,
            cancellationToken: cancellationToken);
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
            throw new JsonException("Promotion payload digest header is invalid.");
        }

        var actualDigest = Convert
            .ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw new JsonException(
                "Promotion payload digest does not match the received message body.");
        }
    }

    internal static void ValidateMessageIdentity(Guid eventId, string? messageId)
    {
        if (eventId == Guid.Empty ||
            !Guid.TryParse(messageId, out var parsedMessageId) ||
            parsedMessageId != eventId)
        {
            throw new JsonException(
                "Promotion message ID must match the producer-owned event identity.");
        }
    }

    internal static bool IsRetryableProjectionFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is QueryProjectionException { StatusCode: 503 } ||
               exception is DbException { IsTransient: true } ||
               exception is TimeoutException ||
               exception is IOException ||
               exception.InnerException is not null &&
               IsRetryableProjectionFailure(exception.InnerException);
    }

    private static string ReadRequiredHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var rawValue) || rawValue is null)
        {
            throw new JsonException($"Required RabbitMQ header '{name}' is absent.");
        }

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

        return value;
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
