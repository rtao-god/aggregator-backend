using Microsoft.Extensions.Configuration;
using Platform.Messaging;

namespace Aggregator.Promotion.Worker;

public sealed record PromotionWorkerOptions
{
    public const string SectionName = "PromotionWorker";

    public required string ConnectionString { get; init; }

    public required Uri BrokerUri { get; init; }

    public required string Exchange { get; init; }

    public required string DispatcherIdentity { get; init; }

    public required Guid SystemActorId { get; init; }

    public int TransitionBatchSize { get; init; } = 100;

    public int OutboxBatchSize { get; init; } = 50;

    public int MaximumDeliveryAttempts { get; init; } = 8;

    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan PollDelay { get; init; } = TimeSpan.FromSeconds(2);

    public static PromotionWorkerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Promotion")
            ?? throw new InvalidOperationException("Connection string 'Promotion' is required.");
        var brokerUriValue = Require(configuration, "Messaging:BrokerUri");
        if (!Uri.TryCreate(brokerUriValue, UriKind.Absolute, out var brokerUri))
        {
            throw new InvalidOperationException("Messaging:BrokerUri must be an absolute URI.");
        }

        if (!Guid.TryParse(Require(configuration, $"{SectionName}:SystemActorId"), out var systemActorId) ||
            systemActorId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"{SectionName}:SystemActorId must be a non-empty UUID.");
        }

        return Create(
            connectionString,
            brokerUri,
            Require(configuration, "Messaging:Exchange"),
            Require(configuration, $"{SectionName}:DispatcherIdentity"),
            systemActorId,
            ReadInt(configuration, $"{SectionName}:TransitionBatchSize", 100),
            ReadInt(configuration, $"{SectionName}:OutboxBatchSize", 50),
            ReadInt(configuration, $"{SectionName}:MaximumDeliveryAttempts", 8),
            TimeSpan.FromSeconds(ReadInt(configuration, $"{SectionName}:LeaseDurationSeconds", 120)),
            TimeSpan.FromMilliseconds(ReadInt(configuration, $"{SectionName}:PollDelayMilliseconds", 2000)));
    }

    public static PromotionWorkerOptions Create(
        string connectionString,
        Uri brokerUri,
        string exchange,
        string dispatcherIdentity,
        Guid systemActorId,
        int transitionBatchSize = 100,
        int outboxBatchSize = 50,
        int maximumDeliveryAttempts = 8,
        TimeSpan? leaseDuration = null,
        TimeSpan? pollDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(brokerUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(exchange);
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatcherIdentity);
        if (systemActorId == Guid.Empty)
        {
            throw new ArgumentException("Promotion system actor ID is required.", nameof(systemActorId));
        }

        var options = new PromotionWorkerOptions
        {
            ConnectionString = connectionString.Trim(),
            BrokerUri = brokerUri,
            Exchange = exchange.Trim(),
            DispatcherIdentity = dispatcherIdentity.Trim(),
            SystemActorId = systemActorId,
            TransitionBatchSize = transitionBatchSize,
            OutboxBatchSize = outboxBatchSize,
            MaximumDeliveryAttempts = maximumDeliveryAttempts,
            LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(2),
            PollDelay = pollDelay ?? TimeSpan.FromSeconds(2),
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (TransitionBatchSize is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(TransitionBatchSize),
                TransitionBatchSize,
                "Promotion transition batch size must be between 1 and 500.");
        }

        if (PollDelay < TimeSpan.FromMilliseconds(100) || PollDelay > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(
                nameof(PollDelay),
                PollDelay,
                "Promotion worker poll delay must be between 100 milliseconds and 5 minutes.");
        }

        CreateOutboxOptions().Validate();
        CreatePublisherOptions().Validate();
    }

    public OutboxDispatcherOptions CreateOutboxOptions() =>
        new()
        {
            ConnectionString = ConnectionString,
            Schema = "messaging",
            DispatcherIdentity = DispatcherIdentity,
            BatchSize = OutboxBatchSize,
            MaximumDeliveryAttempts = MaximumDeliveryAttempts,
            LeaseDuration = LeaseDuration,
            EmptyDelay = PollDelay,
        };

    public RabbitMqPublisherOptions CreatePublisherOptions() =>
        new()
        {
            BrokerUri = BrokerUri,
            Exchange = Exchange,
            ClientProvidedName = DispatcherIdentity,
        };

    private static string Require(IConfiguration configuration, string path) =>
        configuration[path] is { Length: > 0 } value
            ? value.Trim()
            : throw new InvalidOperationException($"Configuration value '{path}' is required.");

    private static int ReadInt(IConfiguration configuration, string path, int defaultValue)
    {
        var value = configuration[path];
        if (value is null)
        {
            return defaultValue;
        }

        return int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration value '{path}' must be an integer.");
    }
}
