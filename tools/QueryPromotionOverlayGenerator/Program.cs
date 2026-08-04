using System.Reflection;
using Aggregator.Query.Application;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var queryApplication = typeof(QueryProjectionService).Assembly;
var promotionContracts = typeof(Aggregator.Promotion.Contracts.PromotionIntegrationEventTypes).Assembly;
var eventType = promotionContracts.GetTypes()
    .Where(type => type.IsPublic)
    .Where(type => type.Name.Contains("Placement", StringComparison.OrdinalIgnoreCase))
    .Where(type => type.Name.Contains("Changed", StringComparison.OrdinalIgnoreCase))
    .OrderByDescending(type => type.Name.Contains("Sponsored", StringComparison.OrdinalIgnoreCase))
    .ThenBy(type => type.Name, StringComparer.Ordinal)
    .FirstOrDefault()
    ?? throw Failure("Promotion contracts expose no public sponsored-placement changed event.");
var projectionMethod = queryApplication.GetTypes()
    .Where(type => type.IsPublic && type.IsClass && !type.IsAbstract)
    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Select(method => new ServiceMethod(type, method)))
    .Where(candidate => candidate.Method.GetParameters().Any(parameter => parameter.ParameterType == eventType))
    .Where(candidate => IsAwaitable(candidate.Method.ReturnType))
    .OrderByDescending(candidate => candidate.Method.Name.Contains("Promotion", StringComparison.OrdinalIgnoreCase))
    .ThenByDescending(candidate => candidate.Method.Name.Contains("Overlay", StringComparison.OrdinalIgnoreCase))
    .FirstOrDefault()
    ?? throw Failure(
        "Query Application exposes no public asynchronous method that accepts the producer-owned " +
        $"event '{eventType.FullName}'. Add the atomic promotion-overlay/PublicReadRevision owner service first.");

var arguments = string.Join(",\n                        ",
    projectionMethod.Method.GetParameters().Select(parameter => MapArgument(parameter, eventType)));
var projectDirectory = Path.Combine(root.FullName, "src", "Query", "Query.PromotionWorker");
var testDirectory = Path.Combine(root.FullName, "tests", "Query", "Query.PromotionWorker.Tests");
Directory.CreateDirectory(projectDirectory);
Directory.CreateDirectory(testDirectory);
File.WriteAllText(
    Path.Combine(projectDirectory, "Query.PromotionWorker.csproj"),
    """
    <Project Sdk="Microsoft.NET.Sdk.Worker">
      <ItemGroup>
        <PackageReference Include="Microsoft.Extensions.Hosting" />
        <PackageReference Include="RabbitMQ.Client" />
      </ItemGroup>
      <ItemGroup>
        <ProjectReference Include="../Query.Application/Query.Application.csproj" />
        <ProjectReference Include="../Query.Infrastructure/Query.Infrastructure.csproj" />
        <ProjectReference Include="../../Promotion/Promotion.Contracts/Promotion.Contracts.csproj" />
        <ProjectReference Include="../../BuildingBlocks/Platform.Observability/Platform.Observability.csproj" />
      </ItemGroup>
    </Project>
    """ + Environment.NewLine);
File.WriteAllText(
    Path.Combine(projectDirectory, "QueryPromotionWorkerOptions.cs"),
    """
    using Microsoft.Extensions.Configuration;

    namespace Aggregator.Query.PromotionWorker;

    public sealed record QueryPromotionWorkerOptions
    {
        public const string SectionName = "QueryPromotionWorker";

        public required Uri BrokerUri { get; init; }
        public required string Exchange { get; init; }
        public required string Queue { get; init; }
        public required string RoutingKey { get; init; }
        public ushort PrefetchCount { get; init; } = 8;

        public static QueryPromotionWorkerOptions FromConfiguration(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var uriValue = Require(configuration, $"{SectionName}:BrokerUri");
            if (!Uri.TryCreate(uriValue, UriKind.Absolute, out var uri))
                throw new InvalidOperationException($"{SectionName}:BrokerUri must be an absolute URI.");
            var options = new QueryPromotionWorkerOptions
            {
                BrokerUri = uri,
                Exchange = configuration[$"{SectionName}:Exchange"] ?? "aggregator.events",
                Queue = configuration[$"{SectionName}:Queue"] ?? "query.promotion-overlay-projection",
                RoutingKey = configuration[$"{SectionName}:RoutingKey"] ?? "promotion.placement.changed",
                PrefetchCount = ushort.TryParse(configuration[$"{SectionName}:PrefetchCount"], out var prefetch)
                    ? prefetch : (ushort)8,
            };
            options.Validate();
            return options;
        }

        public void Validate()
        {
            if (BrokerUri.Scheme is not ("amqp" or "amqps")) throw new ArgumentException("Broker URI must use AMQP.", nameof(BrokerUri));
            ValidateName(Exchange, nameof(Exchange), 255);
            ValidateName(Queue, nameof(Queue), 255);
            ValidateName(RoutingKey, nameof(RoutingKey), 255);
            if (PrefetchCount is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(PrefetchCount));
        }

        private static string Require(IConfiguration configuration, string path) =>
            configuration[path] is { Length: > 0 } value ? value.Trim()
                : throw new InvalidOperationException($"Configuration value '{path}' is required.");

        private static void ValidateName(string value, string parameterName, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
                throw new ArgumentException("AMQP identity is invalid.", parameterName);
        }
    }
    """ + Environment.NewLine);
File.WriteAllText(
    Path.Combine(projectDirectory, "PromotionOverlayProjectionWorker.cs"),
    $$"""
    using System.Text.Json;
    using System.Text.Json.Serialization;
    using {{eventType.Namespace}};
    using Aggregator.Query.Application;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using RabbitMQ.Client;
    using RabbitMQ.Client.Events;

    namespace Aggregator.Query.PromotionWorker;

    public sealed class PromotionOverlayProjectionWorker(
        IServiceScopeFactory scopeFactory,
        QueryPromotionWorkerOptions options) : BackgroundService
    {
        private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var factory = new ConnectionFactory
            {
                Uri = options.BrokerUri,
                ClientProvidedName = options.Queue,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };
            await using var connection = await factory.CreateConnectionAsync(stoppingToken);
            await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
            await channel.ExchangeDeclareAsync(
                options.Exchange,
                ExchangeType.Topic,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);
            var queue = await channel.QueueDeclareAsync(
                options.Queue,
                durable: true,
                exclusive: false,
                autoDelete: false,
                arguments: null,
                cancellationToken: stoppingToken);
            await channel.QueueBindAsync(
                queue.QueueName,
                options.Exchange,
                options.RoutingKey,
                arguments: null,
                cancellationToken: stoppingToken);
            await channel.BasicQosAsync(0, options.PrefetchCount, global: false, cancellationToken: stoppingToken);
            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += async (_, delivery) =>
            {
                try
                {
                    var message = JsonSerializer.Deserialize<{{TypeName(eventType)}}>(
                        delivery.Body.Span,
                        SerializerOptions)
                        ?? throw new JsonException("Promotion placement event deserialized to null.");
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider.GetRequiredService<{{TypeName(projectionMethod.ServiceType)}}>();
                    await service.{{projectionMethod.Method.Name}}(
                        {{arguments}});
                    await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (JsonException)
                {
                    await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, cancellationToken: stoppingToken);
                }
                catch
                {
                    await channel.BasicNackAsync(
                        delivery.DeliveryTag,
                        multiple: false,
                        requeue: true,
                        cancellationToken: stoppingToken);
                }
            };
            await channel.BasicConsumeAsync(
                queue.QueueName,
                autoAck: false,
                consumer,
                cancellationToken: stoppingToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
        }

        private static JsonSerializerOptions CreateSerializerOptions()
        {
            var serializer = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = false,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            serializer.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
            return serializer;
        }
    }
    """ + Environment.NewLine);
File.WriteAllText(
    Path.Combine(projectDirectory, "Program.cs"),
    """
    using Aggregator.Query.Application;
    using Aggregator.Query.Infrastructure;
    using Aggregator.Query.PromotionWorker;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Platform.Observability;

    var builder = Host.CreateApplicationBuilder(args);
    var connectionString = builder.Configuration.GetConnectionString("Query")
        ?? throw new InvalidOperationException("Connection string 'Query' is required.");
    var options = QueryPromotionWorkerOptions.FromConfiguration(builder.Configuration);
    builder.Services.AddSingleton(options);
    builder.Services.AddQueryDatabase(new QueryDatabaseOptions { ConnectionString = connectionString });
    builder.Services.AddQueryPublicReadInfrastructure();
    builder.Services.AddHostedService<PromotionOverlayProjectionWorker>();
    builder.Services.AddPlatformObservability(builder.Configuration, "query-promotion-overlay-worker");
    await builder.Build().RunAsync();
    """ + Environment.NewLine);
File.WriteAllText(
    Path.Combine(testDirectory, "Query.PromotionWorker.Tests.csproj"),
    """
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup>
      <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" />
        <PackageReference Include="xunit" />
        <PackageReference Include="xunit.runner.visualstudio"><PrivateAssets>all</PrivateAssets></PackageReference>
        <PackageReference Include="coverlet.collector"><PrivateAssets>all</PrivateAssets></PackageReference>
      </ItemGroup>
      <ItemGroup><ProjectReference Include="../../../src/Query/Query.PromotionWorker/Query.PromotionWorker.csproj" /></ItemGroup>
    </Project>
    """ + Environment.NewLine);
File.WriteAllText(Path.Combine(testDirectory, "Usings.cs"), "global using Xunit;" + Environment.NewLine);
File.WriteAllText(
    Path.Combine(testDirectory, "QueryPromotionWorkerOptionsTests.cs"),
    """
    using Aggregator.Query.PromotionWorker;

    namespace Query.PromotionWorker.Tests;

    public sealed class QueryPromotionWorkerOptionsTests
    {
        [Fact]
        public void ExactPromotionRoutingContractIsAccepted()
        {
            var options = new QueryPromotionWorkerOptions
            {
                BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
                Exchange = "aggregator.events",
                Queue = "query.promotion-overlay-projection",
                RoutingKey = "promotion.placement.changed",
            };
            options.Validate();
            Assert.Equal((ushort)8, options.PrefetchCount);
        }

        [Fact]
        public void NonAmqpBrokerIsRejected()
        {
            var options = new QueryPromotionWorkerOptions
            {
                BrokerUri = new Uri("https://broker.test"),
                Exchange = "aggregator.events",
                Queue = "query.promotion-overlay-projection",
                RoutingKey = "promotion.placement.changed",
            };
            var exception = Assert.Throws<ArgumentException>(options.Validate);
            Assert.Equal("BrokerUri", exception.ParamName);
        }
    }
    """ + Environment.NewLine);
var reportDirectory = Path.Combine(root.FullName, "docs", "generated");
Directory.CreateDirectory(reportDirectory);
File.WriteAllText(
    Path.Combine(reportDirectory, "query-promotion-worker-generation.md"),
    $"""
    # Query promotion overlay worker generation

    - Producer event: `{eventType.FullName}`.
    - Atomic Query owner method: `{projectionMethod.ServiceType.FullName}.{projectionMethod.Method.Name}`.
    - Queue routing key: `promotion.placement.changed`.
    - Poison JSON is rejected without requeue; typed owner failures remain retryable through the durable queue.
    - The worker does not join Promotion data during public requests.
    """ + Environment.NewLine);

static string MapArgument(ParameterInfo parameter, Type eventType)
{
    if (parameter.ParameterType == eventType) return "message";
    if (parameter.ParameterType == typeof(CancellationToken)) return "stoppingToken";
    if (parameter.ParameterType == typeof(string) &&
        parameter.Name?.Contains("correlation", StringComparison.OrdinalIgnoreCase) == true)
    {
        var property = eventType.GetProperties().FirstOrDefault(candidate =>
            candidate.PropertyType == typeof(string) &&
            candidate.Name.Contains("Correlation", StringComparison.OrdinalIgnoreCase));
        return property is null ? "message.EventId.ToString(\"D\")" : $"message.{property.Name}";
    }
    if (parameter.ParameterType == typeof(Guid) &&
        parameter.Name?.Contains("causation", StringComparison.OrdinalIgnoreCase) == true)
    {
        return "message.EventId";
    }
    throw Failure(
        $"Cannot map Query promotion projection parameter '{parameter.Name}' of type '{parameter.ParameterType}'.");
}

static bool IsAwaitable(Type type) => type == typeof(Task) || type == typeof(ValueTask) ||
    type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) ||
        type.GetGenericTypeDefinition() == typeof(ValueTask<>));

static string TypeName(Type type)
{
    if (!type.IsGenericType) return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
    var name = type.GetGenericTypeDefinition().FullName ?? throw Failure($"Type '{type}' has no full name.");
    name = name[..name.IndexOf('`')].Replace('+', '.');
    return "global::" + name + "<" + string.Join(", ", type.GetGenericArguments().Select(TypeName)) + ">";
}

static DirectoryInfo FindRepositoryRoot(string start)
{
    var current = new DirectoryInfo(start);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "AggregatorBackend.slnx"))) return current;
        current = current.Parent;
    }
    throw Failure("Repository root was not found.");
}

static InvalidOperationException Failure(string message) => new(message);

internal sealed record ServiceMethod(Type ServiceType, MethodInfo Method);
