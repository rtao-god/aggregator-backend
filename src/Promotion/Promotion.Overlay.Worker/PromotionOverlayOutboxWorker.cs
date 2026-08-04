using System.Text;
using Aggregator.Promotion.Overlay.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Aggregator.Promotion.Overlay.Worker;

public sealed class PromotionOverlayOutboxWorker : BackgroundService, IAsyncDisposable
{
    private readonly IPromotionOverlayOutboxStore _store;
    private readonly PromotionOverlayWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PromotionOverlayOutboxWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public PromotionOverlayOutboxWorker(
        IPromotionOverlayOutboxStore store,
        PromotionOverlayWorkerOptions options,
        TimeProvider timeProvider,
        ILogger<PromotionOverlayOutboxWorker> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var lease = await _store.LeaseNextAsync(
                _options.WorkerId,
                _options.LeaseDuration,
                _options.MaximumAttempts,
                stoppingToken);
            if (lease is null)
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
                continue;
            }

            try
            {
                var channel = await GetChannelAsync(stoppingToken);
                var properties = new BasicProperties
                {
                    AppId = _options.WorkerId,
                    ContentType = "application/json",
                    ContentEncoding = "utf-8",
                    DeliveryMode = DeliveryModes.Persistent,
                    MessageId = lease.EventId.ToString("D"),
                    CorrelationId = lease.CorrelationId,
                    Type = lease.ContractIdentity,
                    Timestamp = new AmqpTimestamp(lease.OccurredAtUtc.ToUnixTimeSeconds()),
                    Headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["payload-digest"] = lease.PayloadDigest,
                        ["causation-id"] = lease.CausationId?.ToString("D"),
                    },
                };
                var body = Encoding.UTF8.GetBytes(lease.PayloadJson);
                await channel.BasicPublishAsync(
                    exchange: _options.Exchange,
                    routingKey: lease.RoutingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: body,
                    cancellationToken: stoppingToken);
                await _store.MarkDispatchedAsync(
                    lease.EventId,
                    lease.LeaseToken,
                    _timeProvider.GetUtcNow(),
                    stoppingToken);
                if (_logger.IsEnabled(LogLevel.Information))
                {
                    _logger.LogInformation(
                        "Dispatched Promotion overlay event {EventId} after {DeliveryAttempts} attempt(s)",
                        lease.EventId,
                        lease.DeliveryAttempts);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Promotion overlay event {EventId} delivery attempt {DeliveryAttempts} failed",
                    lease.EventId,
                    lease.DeliveryAttempts);
                await _store.MarkFailedAsync(
                    lease.EventId,
                    lease.LeaseToken,
                    exception.Message,
                    _timeProvider.GetUtcNow(),
                    _options.MaximumAttempts,
                    stoppingToken);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
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

        GC.SuppressFinalize(this);
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
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
                ClientProvidedName = _options.WorkerId,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
            };
            _connection = await factory.CreateConnectionAsync(
                _options.WorkerId,
                cancellationToken);
        }

        _channel = await _connection.CreateChannelAsync(
            new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true),
            cancellationToken);
        await _channel.ExchangeDeclareAsync(
            exchange: _options.Exchange,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken);
        return _channel;
    }
}
