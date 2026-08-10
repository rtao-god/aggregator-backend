using System.Text.Json;
using Aggregator.Analytics.Contracts;
using Aggregator.Promotion.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Aggregator.Promotion.Worker;

/// <summary>Consumes Analytics-owned closed usage revisions into the Promotion-local projection.</summary>
public sealed class PromotionUsageProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions =
        PromotionMessageEnvelopeValidation.CreateSerializerOptions();
    private readonly PromotionUsageProjectionWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PromotionUsageProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public PromotionUsageProjectionWorker(
        PromotionUsageProjectionWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<PromotionUsageProjectionWorker> logger)
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
            ClientProvidedName = "promotion-analytics-usage-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "promotion-analytics-usage-worker",
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
        PromotionUsageProjectionWorkerLog.ConsumerStarted(
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
                "Promotion Analytics usage consumer channel is unavailable.");
        var cancellationToken = eventArgs.CancellationToken;
        try
        {
            PromotionMessageEnvelopeValidation.ValidateEnvelope(
                eventArgs,
                _options.RoutingKey,
                AnalyticsPromotionUsageIntegrationContracts.ContractIdentity,
                "Analytics Promotion usage");
            var payloadDigest = PromotionMessageEnvelopeValidation.ReadRequiredHeader(
                eventArgs.BasicProperties.Headers,
                "payload-digest");
            PromotionMessageEnvelopeValidation.VerifyPayloadIntegrity(
                eventArgs.Body.Span,
                payloadDigest,
                "Analytics Promotion usage");
            var integrationEvent = JsonSerializer.Deserialize<PromotionUsageWindowClosed>(
                    eventArgs.Body.Span,
                    SerializerOptions)
                ?? throw new JsonException(
                    "Analytics Promotion usage payload is empty.");
            var messageId = PromotionMessageEnvelopeValidation.ValidateMessageIdentity(
                integrationEvent.EventId,
                eventArgs.BasicProperties.MessageId,
                "Analytics Promotion usage");
            var correlationId = PromotionMessageEnvelopeValidation.ReadRequiredCorrelationId(
                eventArgs.BasicProperties.CorrelationId);
            var causationId = PromotionMessageEnvelopeValidation.ReadOptionalGuidHeader(
                eventArgs.BasicProperties.Headers,
                "causation-id");
            var projectionMessage = CreateProjectionMessage(
                integrationEvent,
                messageId,
                eventArgs.BasicProperties.Type!,
                payloadDigest,
                correlationId,
                causationId);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider
                .GetRequiredService<ApplyAnalyticsPromotionUsageWindowService>();
            var result = await service.ApplyAsync(
                projectionMessage,
                cancellationToken);
            await channel.BasicAckAsync(
                deliveryTag: eventArgs.DeliveryTag,
                multiple: false,
                cancellationToken: cancellationToken);
            PromotionUsageProjectionWorkerLog.UsageApplied(
                _logger,
                integrationEvent.CatalogKey,
                integrationEvent.PlacementId,
                integrationEvent.WindowStartsAtUtc,
                integrationEvent.AggregateRevision,
                result.Disposition,
                messageId,
                correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (PromotionMessageEnvelopeValidation.IsRetryable(exception))
        {
            PromotionUsageProjectionWorkerLog.TransientFailure(
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
            PromotionUsageProjectionWorkerLog.MessageDeadLettered(
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

    internal static AnalyticsPromotionUsageProjectionMessage CreateProjectionMessage(
        PromotionUsageWindowClosed integrationEvent,
        Guid messageId,
        string contractIdentity,
        string payloadDigest,
        string correlationId,
        Guid? causationId)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return new AnalyticsPromotionUsageProjectionMessage(
            messageId,
            contractIdentity,
            payloadDigest,
            correlationId,
            causationId,
            integrationEvent.EventId,
            integrationEvent.UsageWindowId,
            integrationEvent.PlacementId,
            integrationEvent.ListingId,
            integrationEvent.CatalogKey,
            integrationEvent.WindowStartsAtUtc,
            integrationEvent.WindowEndsAtUtc,
            integrationEvent.AcceptedImpressions,
            integrationEvent.AcceptedListingOpens,
            integrationEvent.AcceptedOutboundClicks,
            integrationEvent.AggregationRunId,
            integrationEvent.AggregateRevision,
            integrationEvent.OccurredAtUtc);
    }

    internal static void VerifyPayloadIntegrity(
        ReadOnlySpan<byte> payload,
        string expectedDigest) =>
        PromotionMessageEnvelopeValidation.VerifyPayloadIntegrity(
            payload,
            expectedDigest,
            "Analytics Promotion usage");

    internal static Guid ValidateMessageIdentity(Guid eventId, string? messageId) =>
        PromotionMessageEnvelopeValidation.ValidateMessageIdentity(
            eventId,
            messageId,
            "Analytics Promotion usage");

    internal static bool IsRetryable(Exception exception) =>
        PromotionMessageEnvelopeValidation.IsRetryable(exception);
}

internal static partial class PromotionUsageProjectionWorkerLog
{
    [LoggerMessage(
        EventId = 4200,
        Level = LogLevel.Information,
        Message = "Promotion usage consumer started for routing key {RoutingKey} on queue {Queue}")]
    public static partial void ConsumerStarted(
        ILogger logger,
        string routingKey,
        string queue);

    [LoggerMessage(
        EventId = 4201,
        Level = LogLevel.Information,
        Message = "Promotion usage {Disposition} for {CatalogKey}/{PlacementId} window {WindowStartsAtUtc} revision {AggregateRevision}; message {MessageId}; correlation {CorrelationId}")]
    public static partial void UsageApplied(
        ILogger logger,
        string catalogKey,
        Guid placementId,
        DateTimeOffset windowStartsAtUtc,
        long aggregateRevision,
        PromotionUsageProjectionDisposition disposition,
        Guid messageId,
        string correlationId);

    [LoggerMessage(
        EventId = 4202,
        Level = LogLevel.Warning,
        Message = "Promotion usage message {MessageId} hit a transient failure and will be requeued")]
    public static partial void TransientFailure(
        ILogger logger,
        Exception exception,
        string? messageId);

    [LoggerMessage(
        EventId = 4203,
        Level = LogLevel.Error,
        Message = "Promotion usage message {MessageId} was dead-lettered")]
    public static partial void MessageDeadLettered(
        ILogger logger,
        Exception exception,
        string? messageId);
}
