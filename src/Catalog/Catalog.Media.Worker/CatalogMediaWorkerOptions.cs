using Microsoft.Extensions.Configuration;
using Platform.Messaging;

namespace Aggregator.Catalog.Media.Worker;

public sealed record CatalogMediaWorkerOptions
{
    public const string SectionName = "CatalogMediaWorker";
    public required string CatalogConnectionString { get; init; }
    public required Uri BrokerUri { get; init; }
    public required string Exchange { get; init; }
    public required string WorkerIdentity { get; init; }
    public required Guid SystemActorId { get; init; }
    public required string ClamAvHost { get; init; }
    public int ClamAvPort { get; init; } = 3310;
    public int MaximumAttempts { get; init; } = 8;
    public int OutboxBatchSize { get; init; } = 50;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan EmptyDelay { get; init; } = TimeSpan.FromSeconds(2);

    public static CatalogMediaWorkerOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var brokerValue = Require(configuration, "Messaging:BrokerUri");
        if (!Uri.TryCreate(brokerValue, UriKind.Absolute, out var brokerUri))
            throw new InvalidOperationException("Messaging:BrokerUri must be an absolute URI.");
        if (!Guid.TryParse(Require(configuration, $"{SectionName}:SystemActorId"), out var actorId) || actorId == Guid.Empty)
            throw new InvalidOperationException($"{SectionName}:SystemActorId must be a non-empty UUID.");
        var options = new CatalogMediaWorkerOptions
        {
            CatalogConnectionString = configuration.GetConnectionString("Catalog")
                ?? throw new InvalidOperationException("Connection string 'Catalog' is required."),
            BrokerUri = brokerUri,
            Exchange = Require(configuration, "Messaging:Exchange"),
            WorkerIdentity = Require(configuration, $"{SectionName}:WorkerIdentity"),
            SystemActorId = actorId,
            ClamAvHost = Require(configuration, $"{SectionName}:ClamAvHost"),
            ClamAvPort = ReadInt(configuration, $"{SectionName}:ClamAvPort", 3310),
            MaximumAttempts = ReadInt(configuration, $"{SectionName}:MaximumAttempts", 8),
            OutboxBatchSize = ReadInt(configuration, $"{SectionName}:OutboxBatchSize", 50),
            LeaseDuration = TimeSpan.FromSeconds(ReadInt(configuration, $"{SectionName}:LeaseDurationSeconds", 300)),
            EmptyDelay = TimeSpan.FromMilliseconds(ReadInt(configuration, $"{SectionName}:EmptyDelayMilliseconds", 2000)),
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(CatalogConnectionString);
        if (BrokerUri.Scheme is not ("amqp" or "amqps")) throw new ArgumentException("Broker URI must use AMQP.", nameof(BrokerUri));
        ValidateIdentity(Exchange, nameof(Exchange), 255);
        ValidateIdentity(WorkerIdentity, nameof(WorkerIdentity), 200);
        ValidateIdentity(ClamAvHost, nameof(ClamAvHost), 255);
        if (SystemActorId == Guid.Empty) throw new ArgumentException("System actor ID is required.", nameof(SystemActorId));
        if (ClamAvPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(ClamAvPort));
        if (MaximumAttempts is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(MaximumAttempts));
        if (LeaseDuration < TimeSpan.FromSeconds(10) || LeaseDuration > TimeSpan.FromMinutes(30))
            throw new ArgumentOutOfRangeException(nameof(LeaseDuration));
        if (EmptyDelay < TimeSpan.FromMilliseconds(100) || EmptyDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(EmptyDelay));
        CreateOutboxOptions().Validate();
        CreatePublisherOptions().Validate();
    }

    public OutboxDispatcherOptions CreateOutboxOptions() => new()
    {
        ConnectionString = CatalogConnectionString,
        Schema = "media_messaging",
        DispatcherIdentity = WorkerIdentity,
        BatchSize = OutboxBatchSize,
        MaximumDeliveryAttempts = MaximumAttempts,
        LeaseDuration = TimeSpan.FromMinutes(2),
        EmptyDelay = EmptyDelay,
    };

    public RabbitMqPublisherOptions CreatePublisherOptions() => new()
    {
        BrokerUri = BrokerUri,
        Exchange = Exchange,
        ClientProvidedName = WorkerIdentity,
    };

    private static string Require(IConfiguration configuration, string path) =>
        configuration[path] is { Length: > 0 } value ? value.Trim()
            : throw new InvalidOperationException($"Configuration value '{path}' is required.");

    private static int ReadInt(IConfiguration configuration, string path, int fallback) =>
        configuration[path] is null ? fallback : int.TryParse(
            configuration[path],
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
                ? parsed
                : throw new InvalidOperationException($"Configuration value '{path}' must be an integer.");

    private static void ValidateIdentity(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("Runtime identity is invalid.", parameterName);
    }
}
