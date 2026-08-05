using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Platform.Observability;
using RabbitMQ.Client;

var builder = Host.CreateApplicationBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Catalog");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Catalog is required.");
}

var section = "Catalog:EventWorker";
var brokerUriText = builder.Configuration[$"{section}:BrokerUri"];
if (string.IsNullOrWhiteSpace(brokerUriText))
{
    throw new InvalidOperationException($"{section}:BrokerUri is required.");
}

var options = new CatalogEventWorkerOptions
{
    BrokerUri = new Uri(brokerUriText, UriKind.Absolute),
    Exchange = builder.Configuration[$"{section}:Exchange"] ?? "aggregator.events",
    WorkerId = builder.Configuration[$"{section}:WorkerId"] ?? "catalog-event-outbox",
    PollInterval = ParseTimeSpan(
        builder.Configuration[$"{section}:PollInterval"],
        TimeSpan.FromSeconds(1),
        $"{section}:PollInterval"),
    LeaseDuration = ParseTimeSpan(
        builder.Configuration[$"{section}:LeaseDuration"],
        TimeSpan.FromSeconds(30),
        $"{section}:LeaseDuration"),
    MaximumAttempts = ParseInteger(
        builder.Configuration[$"{section}:MaximumAttempts"],
        10,
        $"{section}:MaximumAttempts"),
};
options.Validate();

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddPlatformObservability(builder.Configuration, "catalog-event-worker");
builder.Services.AddHostedService<CatalogEventOutboxWorker>();

await builder.Build().RunAsync();

static TimeSpan ParseTimeSpan(string? value, TimeSpan defaultValue, string path) =>
    value is null
        ? defaultValue
        : TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration value '{path}' must be a TimeSpan.");

static int ParseInteger(string? value, int defaultValue, string path) =>
    value is null
        ? defaultValue
        : int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                $"Configuration value '{path}' must be an integer.");

internal sealed record CatalogEventWorkerOptions
{
    public required Uri BrokerUri { get; init; }

    public string Exchange { get; init; } = "aggregator.events";

    public string WorkerId { get; init; } = "catalog-event-outbox";

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromSeconds(30);

    public int MaximumAttempts { get; init; } = 10;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(BrokerUri);
        if (BrokerUri.Scheme is not ("amqp" or "amqps"))
        {
            throw new InvalidOperationException("Catalog event broker URI must use amqp or amqps.");
        }

        if (string.IsNullOrWhiteSpace(Exchange) || string.IsNullOrWhiteSpace(WorkerId))
        {
            throw new InvalidOperationException("Catalog event exchange and worker ID are required.");
        }

        if (PollInterval < TimeSpan.FromMilliseconds(100) || PollInterval > TimeSpan.FromMinutes(1))
        {
            throw new InvalidOperationException("Catalog event poll interval must be between 100 ms and one minute.");
        }

        if (LeaseDuration < TimeSpan.FromSeconds(5) || LeaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new InvalidOperationException("Catalog event lease duration must be between five seconds and five minutes.");
        }

        if (MaximumAttempts is < 1 or > 100)
        {
            throw new InvalidOperationException("Catalog event maximum attempts must be between one and 100.");
        }
    }
}

internal sealed record CatalogOutboxLease(
    Guid EventId,
    Guid LeaseToken,
    string RoutingKey,
    string ContractIdentity,
    string PayloadJson,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId,
    int DeliveryAttempts);

internal sealed class CatalogEventOutboxWorker : BackgroundService, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly CatalogEventWorkerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CatalogEventOutboxWorker> _logger;
    private IConnection? _connection;
    private IChannel? _channel;

    public CatalogEventOutboxWorker(
        NpgsqlDataSource dataSource,
        CatalogEventWorkerOptions options,
        TimeProvider timeProvider,
        ILogger<CatalogEventOutboxWorker> logger)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options.Validate();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var lease = await LeaseNextAsync(stoppingToken);
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
                await channel.BasicPublishAsync(
                    exchange: _options.Exchange,
                    routingKey: lease.RoutingKey,
                    mandatory: true,
                    basicProperties: properties,
                    body: Encoding.UTF8.GetBytes(lease.PayloadJson),
                    cancellationToken: stoppingToken);
                await MarkDispatchedAsync(
                    lease,
                    _timeProvider.GetUtcNow(),
                    stoppingToken);
                CatalogEventWorkerLog.EventDispatched(
                    _logger,
                    lease.EventId,
                    lease.DeliveryAttempts);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                CatalogEventWorkerLog.EventDeliveryFailed(
                    _logger,
                    exception,
                    lease.EventId,
                    lease.DeliveryAttempts);
                await MarkFailedAsync(
                    lease,
                    exception.Message,
                    _timeProvider.GetUtcNow(),
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

    private async Task<CatalogOutboxLease?> LeaseNextAsync(CancellationToken cancellationToken)
    {
        var leaseToken = Guid.CreateVersion7();
        await using var command = _dataSource.CreateCommand("""
            WITH candidate AS
            (
                SELECT message_id
                FROM catalog.outbox_message
                WHERE dispatched_at_utc IS NULL
                  AND dead_lettered_at_utc IS NULL
                  AND delivery_attempts < @maximum_attempts
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= now())
                ORDER BY occurred_at_utc, message_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE catalog.outbox_message AS target
            SET lease_token = @lease_token,
                leased_by = @worker_id,
                lease_expires_at_utc = now() + @lease_duration,
                delivery_attempts = target.delivery_attempts + 1
            FROM candidate
            WHERE target.message_id = candidate.message_id
            RETURNING target.message_id,
                      target.routing_key,
                      target.contract_identity,
                      target.payload_json,
                      target.payload_digest,
                      target.occurred_at_utc,
                      target.correlation_id,
                      target.causation_id,
                      target.delivery_attempts;
            """);
        command.Parameters.AddWithValue("maximum_attempts", NpgsqlDbType.Integer, _options.MaximumAttempts);
        command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, leaseToken);
        command.Parameters.AddWithValue("worker_id", NpgsqlDbType.Varchar, _options.WorkerId);
        command.Parameters.AddWithValue("lease_duration", NpgsqlDbType.Interval, _options.LeaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new CatalogOutboxLease(
            reader.GetGuid(0),
            leaseToken,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.GetInt32(8));
    }

    private async Task MarkDispatchedAsync(
        CatalogOutboxLease lease,
        DateTimeOffset dispatchedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE catalog.outbox_message
            SET dispatched_at_utc = @dispatched_at_utc,
                lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL,
                last_error = NULL
            WHERE message_id = @message_id
              AND lease_token = @lease_token
              AND dispatched_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL;
            """);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, lease.EventId);
        command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, lease.LeaseToken);
        command.Parameters.AddWithValue("dispatched_at_utc", NpgsqlDbType.TimestampTz, dispatchedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Catalog outbox event '{lease.EventId}' was not owned by lease '{lease.LeaseToken}'.");
        }
    }

    private async Task MarkFailedAsync(
        CatalogOutboxLease lease,
        string error,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        await using var command = _dataSource.CreateCommand("""
            UPDATE catalog.outbox_message
            SET last_error = left(@last_error, 2000),
                dead_lettered_at_utc = CASE
                    WHEN delivery_attempts >= @maximum_attempts THEN @failed_at_utc
                    ELSE NULL
                END,
                dead_letter_reason = CASE
                    WHEN delivery_attempts >= @maximum_attempts THEN left(@last_error, 2000)
                    ELSE NULL
                END,
                lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL
            WHERE message_id = @message_id
              AND lease_token = @lease_token
              AND dispatched_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL;
            """);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, lease.EventId);
        command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, lease.LeaseToken);
        command.Parameters.AddWithValue("last_error", NpgsqlDbType.Varchar, error);
        command.Parameters.AddWithValue("maximum_attempts", NpgsqlDbType.Integer, _options.MaximumAttempts);
        command.Parameters.AddWithValue("failed_at_utc", NpgsqlDbType.TimestampTz, failedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Catalog outbox event '{lease.EventId}' failure was not owned by lease '{lease.LeaseToken}'.");
        }
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

internal static partial class CatalogEventWorkerLog
{
    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Information,
        Message = "Dispatched Catalog event {EventId} after {DeliveryAttempts} attempt(s)")]
    public static partial void EventDispatched(
        ILogger logger,
        Guid eventId,
        int deliveryAttempts);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Catalog event {EventId} delivery attempt {DeliveryAttempts} failed")]
    public static partial void EventDeliveryFailed(
        ILogger logger,
        Exception exception,
        Guid eventId,
        int deliveryAttempts);
}
