using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Query.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Aggregator.Query.Worker;

/// <summary>Consumes Catalog safety events and executes the Query block-first projection protocol.</summary>
public sealed class VisibilitySafetyProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly QueryVisibilityWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<VisibilitySafetyProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public VisibilitySafetyProjectionWorker(
        QueryVisibilityWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<VisibilitySafetyProjectionWorker> logger)
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
            ClientProvidedName = "query-visibility-safety-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "query-visibility-safety-worker",
            stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await DeclareTopologyAsync(_channel, stoppingToken);
        await _channel.BasicQosAsync(
            0,
            _options.PrefetchCount,
            global: false,
            cancellationToken: stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageAsync;
        _ = await _channel.BasicConsumeAsync(
            _options.Queue,
            autoAck: false,
            consumer,
            cancellationToken: stoppingToken);
        _logger.LogInformation(
            "Query visibility worker is consuming {RoutingKey} from {Queue}",
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
            ?? throw new InvalidOperationException("Query visibility worker channel is unavailable.");
        var cancellationToken = eventArgs.CancellationToken;
        try
        {
            var redeliveryCount = GetRedeliveryCount(eventArgs);
            if (redeliveryCount >= _options.DeliveryLimit)
            {
                throw new JsonException(
                    $"Catalog visibility event exceeded delivery limit '{_options.DeliveryLimit}'.");
            }

            if (!string.Equals(
                    eventArgs.BasicProperties.Type,
                    CatalogIntegrationEventContracts.PublicVisibilitySuppressionChanged,
                    StringComparison.Ordinal))
            {
                throw new JsonException(
                    $"Catalog visibility event contract '{eventArgs.BasicProperties.Type}' is unsupported.");
            }

            var payloadDigest = ReadRequiredHeader(
                eventArgs.BasicProperties.Headers,
                "payload-digest");
            VerifyPayloadIntegrity(eventArgs.Body.Span, payloadDigest);
            var change = JsonSerializer.Deserialize<CatalogPublicVisibilitySuppressionChanged>(
                eventArgs.Body.Span,
                SerializerOptions)
                ?? throw new JsonException("Catalog visibility change payload is empty.");
            ValidateMessageIdentity(change.EventId, eventArgs.BasicProperties.MessageId);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<VisibilitySafetyProjectionService>();
            var result = await service.ApplyAsync(
                change,
                payloadDigest,
                ReadRequiredCorrelationId(eventArgs.BasicProperties.CorrelationId),
                cancellationToken);
            await channel.BasicAckAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: cancellationToken);
            _logger.LogInformation(
                "Applied Catalog visibility suppression {SuppressionId} revision {Revision} as public read revision {PublicReadRevisionId}; disposition={Disposition}",
                change.SuppressionId,
                change.AggregateRevision,
                result.PublicReadRevision.Id,
                result.Disposition);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsRetryable(exception))
        {
            _logger.LogWarning(
                exception,
                "Requeueing transient Catalog visibility event {MessageId}",
                eventArgs.BasicProperties.MessageId);
            await Task.Delay(_options.RetryDelay, cancellationToken);
            await channel.BasicRejectAsync(
                eventArgs.DeliveryTag,
                requeue: true,
                cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Dead-lettering Catalog visibility event {MessageId}",
                eventArgs.BasicProperties.MessageId);
            await channel.BasicNackAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                requeue: false,
                cancellationToken: cancellationToken);
        }
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(
            _options.Exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(
            _options.DeadLetterExchange,
            ExchangeType.Topic,
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
            _options.DeadLetterQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: deadLetterQueueArguments,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            _options.DeadLetterQueue,
            _options.DeadLetterExchange,
            _options.RoutingKey,
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
            _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArguments,
            cancellationToken: cancellationToken);
        await channel.QueueBindAsync(
            _options.Queue,
            _options.Exchange,
            _options.RoutingKey,
            arguments: null,
            cancellationToken: cancellationToken);
    }

    internal static void VerifyPayloadIntegrity(ReadOnlySpan<byte> payload, string expectedDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest) ||
            expectedDigest.Length != 64 ||
            expectedDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new JsonException("Catalog visibility payload digest header is invalid.");
        }

        var actualDigest = Convert
            .ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();
        if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
        {
            throw new JsonException(
                "Catalog visibility payload digest does not match the message body.");
        }
    }

    internal static void ValidateMessageIdentity(Guid eventId, string? messageId)
    {
        if (eventId == Guid.Empty ||
            !Guid.TryParse(messageId, out var parsedMessageId) ||
            parsedMessageId != eventId)
        {
            throw new JsonException(
                "Catalog visibility message ID must match the producer-owned event identity.");
        }
    }

    internal static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is QueryProjectionException { StatusCode: 503 } ||
               exception is DbException { IsTransient: true } ||
               exception is TimeoutException ||
               exception is IOException ||
               exception.InnerException is not null && IsRetryable(exception.InnerException);
    }

    internal static int GetRedeliveryCount(BasicDeliverEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        if (eventArgs.BasicProperties.Headers is not { } headers ||
            !headers.TryGetValue("x-delivery-count", out var rawValue) ||
            rawValue is null)
        {
            return eventArgs.Redelivered ? 1 : 0;
        }

        return rawValue switch
        {
            byte value => value,
            sbyte value => value,
            short value => value,
            int value => value,
            long value when value <= int.MaxValue => (int)value,
            byte[] bytes when int.TryParse(
                Encoding.UTF8.GetString(bytes),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => throw new JsonException("RabbitMQ x-delivery-count header is invalid."),
        };
    }


    private static string ReadRequiredCorrelationId(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw new JsonException(
                "RabbitMQ correlation ID is absent or exceeds the Query contract limit.");
        }

        return correlationId.Trim();
    }

    private static string ReadRequiredHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null || !headers.TryGetValue(name, out var raw) || raw is null)
        {
            throw new JsonException($"Required RabbitMQ header '{name}' is absent.");
        }

        var value = raw switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            string text => text,
            _ => throw new JsonException(
                $"RabbitMQ header '{name}' has an unsupported type."),
        };
        return string.IsNullOrWhiteSpace(value)
            ? throw new JsonException($"RabbitMQ header '{name}' is empty.")
            : value;
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
