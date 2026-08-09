using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Analytics.Application;
using Aggregator.Query.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Aggregator.Analytics.Worker;

/// <summary>Consumes Query activations into the Analytics-local public-reference projection.</summary>
public sealed class AnalyticsPublicReadProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly AnalyticsPublicReadProjectionWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AnalyticsPublicReadProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public AnalyticsPublicReadProjectionWorker(
        AnalyticsPublicReadProjectionWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<AnalyticsPublicReadProjectionWorker> logger)
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
            ClientProvidedName = "analytics-public-read-projection-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "analytics-public-read-projection-worker",
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
        AnalyticsPublicReadProjectionWorkerLog.ConsumerStarted(
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
            ?? throw new InvalidOperationException("Analytics worker channel is unavailable.");
        var cancellationToken = eventArgs.CancellationToken;
        try
        {
            ValidateEnvelope(eventArgs);
            var payloadDigest = ReadRequiredHeader(
                eventArgs.BasicProperties.Headers,
                "payload-digest");
            var causationId = ReadRequiredGuidHeader(
                eventArgs.BasicProperties.Headers,
                "causation-id");
            VerifyPayloadIntegrity(eventArgs.Body.Span, payloadDigest);
            var activation = JsonSerializer.Deserialize<PublicReadRevisionActivated>(
                eventArgs.Body.Span,
                SerializerOptions)
                ?? throw new JsonException("Query public-read activation payload is empty.");
            ValidateMessageIdentity(activation.EventId, eventArgs.BasicProperties.MessageId);
            var correlationId = ReadRequiredCorrelationId(
                eventArgs.BasicProperties.CorrelationId);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<ApplyPublicReadRevisionActivationService>();
            var result = await service.ApplyAsync(
                activation,
                payloadDigest,
                correlationId,
                cancellationToken);
            await channel.BasicAckAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: cancellationToken);
            AnalyticsPublicReadProjectionWorkerLog.ActivationApplied(
                _logger,
                activation.ActivationRevision,
                activation.PublicReadRevisionId,
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
            AnalyticsPublicReadProjectionWorkerLog.TransientFailure(
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
            AnalyticsPublicReadProjectionWorkerLog.MessageDeadLettered(
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
                $"Query event routing key '{eventArgs.RoutingKey}' is unsupported.");
        }

        if (!string.Equals(
                eventArgs.BasicProperties.Type,
                QueryIntegrationEventContracts.PublicReadRevisionActivated,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Query event contract '{eventArgs.BasicProperties.Type}' is unsupported.");
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
                "Query public-read activation must use application/json with utf-8 encoding.");
        }
    }

    internal static void VerifyPayloadIntegrity(
        ReadOnlySpan<byte> payload,
        string expectedDigest) =>
        AnalyticsMessageEnvelopeValidation.VerifyPayloadIntegrity(
            payload,
            expectedDigest,
            "Query public-read activation");

    internal static void ValidateMessageIdentity(Guid eventId, string? messageId) =>
        AnalyticsMessageEnvelopeValidation.ValidateMessageIdentity(
            eventId,
            messageId,
            "Query public-read activation");

    internal static bool IsRetryable(Exception exception) =>
        AnalyticsMessageEnvelopeValidation.IsRetryable(exception);

    private static string ReadRequiredCorrelationId(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw new JsonException(
                "RabbitMQ correlation ID is absent or exceeds the Analytics contract limit.");
        }

        return correlationId.Trim();
    }

    private static Guid ReadRequiredGuidHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        var value = ReadRequiredHeader(headers, name);
        return Guid.TryParse(value, out var identifier) && identifier != Guid.Empty
            ? identifier
            : throw new JsonException(
                $"RabbitMQ header '{name}' must contain a non-empty UUID.");
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

internal static partial class AnalyticsPublicReadProjectionWorkerLog
{
    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Information,
        Message = "Analytics public-reference consumer is reading {RoutingKey} from {Queue}.")]
    public static partial void ConsumerStarted(
        ILogger logger,
        string routingKey,
        string queue);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Information,
        Message = "Analytics applied public-read activation {ActivationRevision} for revision {PublicReadRevisionId}; disposition={Disposition}; event={EventId}; causation={CausationId}; correlation={CorrelationId}.")]
    public static partial void ActivationApplied(
        ILogger logger,
        long activationRevision,
        Guid publicReadRevisionId,
        PublicReadActivationDisposition disposition,
        Guid eventId,
        Guid causationId,
        string correlationId);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Warning,
        Message = "Analytics is requeueing transient Query public-read event {MessageId}.")]
    public static partial void TransientFailure(
        ILogger logger,
        Exception exception,
        string? messageId);

    [LoggerMessage(
        EventId = 2104,
        Level = LogLevel.Error,
        Message = "Analytics dead-lettered invalid or non-transient Query public-read event {MessageId}.")]
    public static partial void MessageDeadLettered(
        ILogger logger,
        Exception exception,
        string? messageId);
}
