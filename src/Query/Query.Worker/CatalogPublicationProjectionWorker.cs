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

public sealed class CatalogPublicationProjectionWorker : BackgroundService
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly QueryWorkerOptions _options;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CatalogPublicationProjectionWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public CatalogPublicationProjectionWorker(
        QueryWorkerOptions options,
        IServiceScopeFactory scopeFactory,
        ILogger<CatalogPublicationProjectionWorker> logger)
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
            ClientProvidedName = "query-publication-projection-worker",
            RequestedHeartbeat = TimeSpan.FromSeconds(30),
        };
        _connection = await factory.CreateConnectionAsync(
            "query-publication-projection-worker",
            stoppingToken);
        _channel = await _connection.CreateChannelAsync(cancellationToken: stoppingToken);
        await _channel.ExchangeDeclareAsync(
            _options.Exchange,
            ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            noWait: false,
            stoppingToken);
        await _channel.QueueDeclareAsync(
            _options.Queue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            noWait: false,
            stoppingToken);
        await _channel.QueueBindAsync(
            _options.Queue,
            _options.Exchange,
            _options.RoutingKey,
            arguments: null,
            noWait: false,
            stoppingToken);
        await _channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: _options.PrefetchCount,
            global: false,
            stoppingToken);

        var consumer = new AsyncEventingBasicConsumer(_channel);
        consumer.ReceivedAsync += OnMessageAsync;
        _ = await _channel.BasicConsumeAsync(
            _options.Queue,
            autoAck: false,
            consumer,
            stoppingToken);
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Query projection worker is consuming {RoutingKey} from {Queue}",
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
        ArgumentNullException.ThrowIfNull(eventArgs);
        var channel = _channel
            ?? throw new InvalidOperationException("Query worker channel is not available.");
        try
        {
            var activation = JsonSerializer.Deserialize<CatalogPublicationActivated>(
                eventArgs.Body.Span,
                SerializerOptions)
                ?? throw new JsonException("Catalog publication activation payload is empty.");
            var payloadDigest = ReadRequiredHeader(eventArgs.BasicProperties.Headers, "payload-digest");
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<QueryProjectionService>();
            var result = await service.ApplyPublicationAsync(
                activation,
                payloadDigest,
                CancellationToken.None);
            await channel.BasicAckAsync(eventArgs.DeliveryTag, multiple: false, CancellationToken.None);
            if (_logger.IsEnabled(LogLevel.Information))
            {
                _logger.LogInformation(
                    "Applied Catalog activation revision {ActivationRevision} as public read revision {PublicReadRevisionId}; replayed={Replayed}",
                    activation.ActivationRevision,
                    result.PublicReadRevision.Id,
                    result.Replayed);
            }
        }
        catch (Exception exception) when (exception is QueryProjectionException or JsonException or ArgumentException)
        {
            _logger.LogError(
                exception,
                "Rejected invalid Catalog publication activation message {MessageId}",
                eventArgs.BasicProperties.MessageId);
            await channel.BasicNackAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                requeue: false,
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(
                exception,
                "Query publication projection failed for message {MessageId}; message will be retried",
                eventArgs.BasicProperties.MessageId);
            await channel.BasicNackAsync(
                eventArgs.DeliveryTag,
                multiple: false,
                requeue: true,
                CancellationToken.None);
        }
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
            _ => throw new JsonException($"RabbitMQ header '{name}' has an unsupported value type."),
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
