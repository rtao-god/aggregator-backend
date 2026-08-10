using System.Text.Json;
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
    private static readonly JsonSerializerOptions SerializerOptions =
        PromotionMessageEnvelopeValidation.CreateSerializerOptions();
    private readonly PromotionEligibilityProjectionWorkerOptions _options;
    private readonly PromotionWorkerOptions _ownerOptions;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PromotionEligibilityProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public PromotionEligibilityProjectionWorker(
        PromotionEligibilityProjectionWorkerOptions options,
        PromotionWorkerOptions ownerOptions,
        IServiceScopeFactory scopeFactory,
        ILogger<PromotionEligibilityProjectionWorker> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownerOptions = ownerOptions ?? throw new ArgumentNullException(nameof(ownerOptions));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
        _ownerOptions.Validate();
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
            PromotionMessageEnvelopeValidation.ValidateEnvelope(
                eventArgs,
                _options.RoutingKey,
                CatalogIntegrationEventContracts.ListingPromotionEligibilityChanged,
                "Catalog listing eligibility");
            var payloadDigest = PromotionMessageEnvelopeValidation.ReadRequiredHeader(
                eventArgs.BasicProperties.Headers,
                "payload-digest");
            PromotionMessageEnvelopeValidation.VerifyPayloadIntegrity(
                eventArgs.Body.Span,
                payloadDigest,
                "Catalog listing eligibility");
            var integrationEvent = JsonSerializer.Deserialize<
                    CatalogListingPromotionEligibilityChanged>(
                    eventArgs.Body.Span,
                    SerializerOptions)
                ?? throw new JsonException(
                    "Catalog listing eligibility payload is empty.");
            var messageId = PromotionMessageEnvelopeValidation.ValidateMessageIdentity(
                integrationEvent.EventId,
                eventArgs.BasicProperties.MessageId,
                "Catalog eligibility");
            var correlationId = PromotionMessageEnvelopeValidation.ReadRequiredCorrelationId(
                eventArgs.BasicProperties.CorrelationId);
            var causationId = PromotionMessageEnvelopeValidation.ReadOptionalGuidHeader(
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
                PromotionActor.Create(_ownerOptions.SystemActorId),
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
        catch (Exception exception) when (PromotionMessageEnvelopeValidation.IsRetryable(exception))
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

    internal static void VerifyPayloadIntegrity(
        ReadOnlySpan<byte> payload,
        string expectedDigest) =>
        PromotionMessageEnvelopeValidation.VerifyPayloadIntegrity(
            payload,
            expectedDigest,
            "Catalog eligibility");

    internal static Guid ValidateMessageIdentity(Guid eventId, string? messageId) =>
        PromotionMessageEnvelopeValidation.ValidateMessageIdentity(
            eventId,
            messageId,
            "Catalog eligibility");

    internal static bool IsRetryable(Exception exception) =>
        PromotionMessageEnvelopeValidation.IsRetryable(exception);

    internal static Guid? ReadOptionalGuidHeader(
        IDictionary<string, object?>? headers,
        string name) =>
        PromotionMessageEnvelopeValidation.ReadOptionalGuidHeader(headers, name);
}

internal static partial class PromotionEligibilityProjectionWorkerLog
{
    [LoggerMessage(
        EventId = 4100,
        Level = LogLevel.Information,
        Message = "Promotion eligibility consumer started for routing key {RoutingKey} on queue {Queue}")]
    public static partial void ConsumerStarted(
        ILogger logger,
        string routingKey,
        string queue);

    [LoggerMessage(
        EventId = 4101,
        Level = LogLevel.Information,
        Message = "Promotion eligibility {ApplyResult} for {CatalogKey}/{ListingId} revision {EligibilityRevision}; message {MessageId}; correlation {CorrelationId}")]
    public static partial void EligibilityApplied(
        ILogger logger,
        string catalogKey,
        Guid listingId,
        long eligibilityRevision,
        PromotionEligibilityProjectionApplyResult applyResult,
        Guid messageId,
        string correlationId);

    [LoggerMessage(
        EventId = 4102,
        Level = LogLevel.Warning,
        Message = "Promotion eligibility message {MessageId} hit a transient failure and will be requeued")]
    public static partial void TransientFailure(
        ILogger logger,
        Exception exception,
        string? messageId);

    [LoggerMessage(
        EventId = 4103,
        Level = LogLevel.Error,
        Message = "Promotion eligibility message {MessageId} was dead-lettered")]
    public static partial void MessageDeadLettered(
        ILogger logger,
        Exception exception,
        string? messageId);
}
