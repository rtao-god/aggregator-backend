using System.Reflection;
using System.Text;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var applicationAssembly = typeof(SubmitInteractionEventService).Assembly;
var interactionType = typeof(InteractionEvent);
var aggregateType = typeof(DailyListingMetrics);
var interfaces = applicationAssembly.GetTypes()
    .Where(type => type.IsInterface && type.IsPublic)
    .OrderBy(type => type.FullName, StringComparer.Ordinal)
    .ToArray();

var source = interfaces
    .SelectMany(type => type.GetMethods().Select(method => new PortMethod(type, method)))
    .Where(candidate => ReturnsInteractionCollection(candidate.Method, interactionType))
    .OrderByDescending(candidate => ScoreSource(candidate.Method))
    .FirstOrDefault()
    ?? throw Failure(
        "No public Analytics application port returns a typed collection of InteractionEvent. " +
        "Add an explicit unaggregated-event read port before introducing the worker.");
var sink = interfaces
    .SelectMany(type => type.GetMethods().Select(method => new PortMethod(type, method)))
    .Where(candidate => candidate.Method.GetParameters().Any(parameter => parameter.ParameterType == aggregateType))
    .Where(candidate => IsAwaitable(candidate.Method.ReturnType))
    .OrderByDescending(candidate => ScoreSink(candidate.Method))
    .FirstOrDefault()
    ?? throw Failure(
        "No public Analytics application port persists DailyListingMetrics. " +
        "Add an explicit aggregate write port before introducing the worker.");

var staticBuilder = aggregateType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(method => method.ReturnType == aggregateType)
    .Where(method => method.Name is "Create" or "Start" or "Build" or "FromEvents")
    .Where(method => method.GetParameters().Any(parameter => IsInteractionCollection(parameter.ParameterType, interactionType)))
    .OrderByDescending(method => method.Name == "FromEvents")
    .FirstOrDefault();
var instanceFactory = aggregateType.GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(method => method.ReturnType == aggregateType)
    .Where(method => method.Name is "Create" or "Start" or "Begin")
    .Where(method => method.GetParameters().All(parameter => !IsInteractionCollection(parameter.ParameterType, interactionType)))
    .OrderByDescending(method => method.Name == "Create")
    .FirstOrDefault();
var accumulator = aggregateType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .Where(method => method.GetParameters().Length == 1)
    .Where(method => method.GetParameters()[0].ParameterType == interactionType)
    .Where(method => method.Name is "Record" or "Apply" or "Accumulate" or "Add")
    .OrderBy(method => method.Name, StringComparer.Ordinal)
    .FirstOrDefault();
if (staticBuilder is null && (instanceFactory is null || accumulator is null))
{
    throw Failure(
        "DailyListingMetrics exposes neither a typed static event builder nor a Create/Start factory plus " +
        "InteractionEvent accumulator. Add an explicit domain aggregation API instead of duplicating metrics logic in a worker.");
}

var listingProperty = interactionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(property => property.PropertyType == typeof(Guid))
    .OrderByDescending(property => property.Name.Equals("ListingId", StringComparison.Ordinal))
    .ThenByDescending(property => property.Name.Contains("Listing", StringComparison.Ordinal))
    .FirstOrDefault()
    ?? throw Failure("InteractionEvent has no public Guid Listing identity property.");
var occurredProperty = interactionType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
    .Where(property => property.PropertyType == typeof(DateTimeOffset))
    .OrderByDescending(property => property.Name.Equals("OccurredAtUtc", StringComparison.Ordinal))
    .ThenByDescending(property => property.Name.Contains("Occurred", StringComparison.Ordinal))
    .ThenByDescending(property => property.Name.Contains("Event", StringComparison.Ordinal))
    .FirstOrDefault()
    ?? throw Failure("InteractionEvent has no public DateTimeOffset occurrence property.");

var workerDirectory = root / "src" / "Analytics" / "Analytics.Worker";
var testDirectory = root / "tests" / "Analytics" / "Analytics.Worker.Tests";
Directory.CreateDirectory(workerDirectory);
Directory.CreateDirectory(testDirectory);
File.WriteAllText(
    workerDirectory / "Analytics.Worker.csproj",
    """
    <Project Sdk="Microsoft.NET.Sdk.Worker">
      <ItemGroup>
        <PackageReference Include="Microsoft.Extensions.Hosting" />
      </ItemGroup>
      <ItemGroup>
        <ProjectReference Include="../Analytics.Application/Analytics.Application.csproj" />
        <ProjectReference Include="../Analytics.Infrastructure/Analytics.Infrastructure.csproj" />
        <ProjectReference Include="../../BuildingBlocks/Platform.Observability/Platform.Observability.csproj" />
      </ItemGroup>
    </Project>
    """ + Environment.NewLine);
File.WriteAllText(
    workerDirectory / "GeneratedAnalyticsAggregationService.cs",
    GenerateAggregationService(
        source,
        sink,
        staticBuilder,
        instanceFactory,
        accumulator,
        listingProperty,
        occurredProperty));
File.WriteAllText(
    workerDirectory / "AnalyticsWorkerOptions.cs",
    """
    namespace Aggregator.Analytics.Worker;

    public sealed record AnalyticsWorkerOptions
    {
        public const string SectionName = "AnalyticsWorker";

        public int BatchSize { get; init; } = 5000;

        public int CompletedDayLag { get; init; } = 1;

        public TimeSpan PollDelay { get; init; } = TimeSpan.FromMinutes(5);

        public static AnalyticsWorkerOptions FromConfiguration(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            var options = new AnalyticsWorkerOptions
            {
                BatchSize = ReadInt(configuration, $"{SectionName}:BatchSize", 5000),
                CompletedDayLag = ReadInt(configuration, $"{SectionName}:CompletedDayLag", 1),
                PollDelay = TimeSpan.FromSeconds(ReadInt(configuration, $"{SectionName}:PollDelaySeconds", 300)),
            };
            options.Validate();
            return options;
        }

        public void Validate()
        {
            if (BatchSize is < 1 or > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize,
                    "Analytics aggregation batch size must be between 1 and 100000.");
            }

            if (CompletedDayLag is < 1 or > 31)
            {
                throw new ArgumentOutOfRangeException(nameof(CompletedDayLag), CompletedDayLag,
                    "Analytics completed-day lag must be between 1 and 31 days.");
            }

            if (PollDelay < TimeSpan.FromSeconds(10) || PollDelay > TimeSpan.FromHours(24))
            {
                throw new ArgumentOutOfRangeException(nameof(PollDelay), PollDelay,
                    "Analytics worker poll delay must be between 10 seconds and 24 hours.");
            }
        }

        private static int ReadInt(IConfiguration configuration, string path, int defaultValue)
        {
            var value = configuration[path];
            if (value is null)
            {
                return defaultValue;
            }

            return int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new InvalidOperationException($"Configuration value '{path}' must be an integer.");
        }
    }
    """ + Environment.NewLine);
File.WriteAllText(
    workerDirectory / "AnalyticsAggregationWorker.cs",
    """
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;

    namespace Aggregator.Analytics.Worker;

    public sealed class AnalyticsAggregationWorker(
        IServiceScopeFactory scopeFactory,
        AnalyticsWorkerOptions options) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<GeneratedAnalyticsAggregationService>();
                var now = TimeProvider.System.GetUtcNow();
                var day = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-options.CompletedDayLag);
                var aggregated = await service.AggregateAsync(day, options.BatchSize, stoppingToken);
                if (aggregated == 0)
                {
                    await Task.Delay(options.PollDelay, stoppingToken);
                }
            }
        }
    }
    """ + Environment.NewLine);
File.WriteAllText(
    workerDirectory / "Program.cs",
    """
    using Aggregator.Analytics.Application;
    using Aggregator.Analytics.Infrastructure;
    using Aggregator.Analytics.Worker;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Hosting;
    using Platform.Observability;

    var builder = Host.CreateApplicationBuilder(args);
    var options = AnalyticsWorkerOptions.FromConfiguration(builder.Configuration);
    builder.Services.AddSingleton(options);
    builder.Services.AddAnalyticsApplication();
    builder.Services.AddAnalyticsInfrastructure(builder.Configuration);
    builder.Services.AddScoped<GeneratedAnalyticsAggregationService>();
    builder.Services.AddHostedService<AnalyticsAggregationWorker>();
    builder.Services.AddPlatformObservability(builder.Configuration, "analytics-aggregation-worker");

    await builder.Build().RunAsync();
    """ + Environment.NewLine);
File.WriteAllText(
    testDirectory / "Analytics.Worker.Tests.csproj",
    """
    <Project Sdk="Microsoft.NET.Sdk">
      <PropertyGroup><IsPackable>false</IsPackable><IsTestProject>true</IsTestProject></PropertyGroup>
      <ItemGroup>
        <PackageReference Include="Microsoft.NET.Test.Sdk" />
        <PackageReference Include="xunit" />
        <PackageReference Include="xunit.runner.visualstudio"><PrivateAssets>all</PrivateAssets></PackageReference>
        <PackageReference Include="coverlet.collector"><PrivateAssets>all</PrivateAssets></PackageReference>
      </ItemGroup>
      <ItemGroup><ProjectReference Include="../../../src/Analytics/Analytics.Worker/Analytics.Worker.csproj" /></ItemGroup>
    </Project>
    """ + Environment.NewLine);
File.WriteAllText(testDirectory / "Usings.cs", "global using Xunit;" + Environment.NewLine);
File.WriteAllText(
    testDirectory / "AnalyticsWorkerOptionsTests.cs",
    """
    using Aggregator.Analytics.Worker;

    namespace Analytics.Worker.Tests;

    public sealed class AnalyticsWorkerOptionsTests
    {
        [Fact]
        public void DefaultsRepresentCompletedDayAggregation()
        {
            var options = new AnalyticsWorkerOptions();
            options.Validate();
            Assert.Equal(1, options.CompletedDayLag);
            Assert.InRange(options.BatchSize, 1, 100000);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(100001)]
        public void InvalidBatchSizeIsRejected(int batchSize)
        {
            var options = new AnalyticsWorkerOptions { BatchSize = batchSize };
            var exception = Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
            Assert.Equal("BatchSize", exception.ParamName);
        }
    }
    """ + Environment.NewLine);

var reportDirectory = root / "docs" / "generated";
Directory.CreateDirectory(reportDirectory);
File.WriteAllText(
    reportDirectory / "analytics-worker-generation.md",
    $"""
    # Analytics worker generation

    - Event source port: `{source.Interface.FullName}.{source.Method.Name}`.
    - Aggregate sink port: `{sink.Interface.FullName}.{sink.Method.Name}`.
    - Listing identity: `InteractionEvent.{listingProperty.Name}`.
    - Event time: `InteractionEvent.{occurredProperty.Name}`.
    - Domain aggregation: `{(staticBuilder is not null ? staticBuilder.Name : $"{instanceFactory!.Name} + {accumulator!.Name}")}`.
    - Runtime reflection is not used by generated production code.
    """ + Environment.NewLine);

static string GenerateAggregationService(
    PortMethod source,
    PortMethod sink,
    MethodInfo? staticBuilder,
    MethodInfo? instanceFactory,
    MethodInfo? accumulator,
    PropertyInfo listingProperty,
    PropertyInfo occurredProperty)
{
    var sourceArguments = string.Join(",\n            ",
        source.Method.GetParameters().Select(MapSourceParameter));
    var sinkArguments = string.Join(",\n                ",
        sink.Method.GetParameters().Select(MapSinkParameter));
    var sourceInterface = TypeName(source.Interface);
    var sinkInterface = TypeName(sink.Interface);
    var builderCode = staticBuilder is not null
        ? $"var aggregate = DailyListingMetrics.{staticBuilder.Name}(\n                {string.Join(",\n                ", staticBuilder.GetParameters().Select(MapFactoryParameter))});"
        : $"""
          var aggregate = DailyListingMetrics.{instanceFactory!.Name}(
                {string.Join(",\n                ", instanceFactory.GetParameters().Select(MapFactoryParameter))});
          foreach (var interaction in group.OrderBy(item => item.{occurredProperty.Name}))
          {{
              _ = aggregate.{accumulator!.Name}(interaction);
          }}
          """;
    return $$"""
    using Aggregator.Analytics.Application;
    using Aggregator.Analytics.Domain;

    namespace Aggregator.Analytics.Worker;

    public sealed class GeneratedAnalyticsAggregationService(
        {{sourceInterface}} source,
        {{sinkInterface}} sink)
    {
        public async Task<int> AggregateAsync(
            DateOnly day,
            int batchSize,
            CancellationToken cancellationToken)
        {
            if (batchSize is < 1 or > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(batchSize));
            }

            var startUtc = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var endUtc = startUtc.AddDays(1);
            var events = await source.{{source.Method.Name}}(
            {{sourceArguments}});
            var relevant = events
                .Where(item => item.{{occurredProperty.Name}} >= startUtc && item.{{occurredProperty.Name}} < endUtc)
                .Take(batchSize)
                .ToArray();
            var aggregates = 0;
            foreach (var group in relevant.GroupBy(item => item.{{listingProperty.Name}}).OrderBy(group => group.Key))
            {
                {{builderCode}}
                await sink.{{sink.Method.Name}}(
                {{sinkArguments}});
                aggregates++;
            }

            return aggregates;
        }
    }
    """ + Environment.NewLine;

    string MapSourceParameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var name = parameter.Name?.ToLowerInvariant() ?? string.Empty;
        if (type == typeof(CancellationToken)) return "cancellationToken";
        if (type == typeof(DateOnly)) return name.Contains("to") || name.Contains("end") ? "day.AddDays(1)" : "day";
        if (type == typeof(DateTimeOffset)) return name.Contains("to") || name.Contains("end") ? "endUtc" : "startUtc";
        if (type == typeof(int)) return "batchSize";
        throw Failure($"Cannot map event-source parameter '{parameter.Name}' of type '{type}'.");
    }

    string MapSinkParameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(DailyListingMetrics)) return "aggregate";
        if (type == typeof(CancellationToken)) return "cancellationToken";
        if (type == typeof(long))
        {
            var revision = typeof(DailyListingMetrics).GetProperty("AggregateRevision")
                ?? typeof(DailyListingMetrics).GetProperty("Revision");
            return revision is null ? "0L" : $"aggregate.{revision.Name}";
        }
        throw Failure($"Cannot map aggregate-sink parameter '{parameter.Name}' of type '{type}'.");
    }

    string MapFactoryParameter(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var name = parameter.Name?.ToLowerInvariant() ?? string.Empty;
        if (IsInteractionCollection(type, typeof(InteractionEvent))) return "group.ToArray()";
        if (type == typeof(Guid)) return "group.Key";
        if (type == typeof(DateOnly)) return "day";
        if (type == typeof(DateTimeOffset))
        {
            if (name.Contains("start") || name.Contains("from")) return "startUtc";
            if (name.Contains("end") || name.Contains("to")) return "endUtc";
            return "TimeProvider.System.GetUtcNow()";
        }
        if (type == typeof(int)) return "0";
        if (type == typeof(long)) return "0L";
        if (type == typeof(bool)) return "false";
        if (type.IsEnum)
        {
            var complete = Enum.GetNames(type).FirstOrDefault(value => value.Equals("Complete", StringComparison.OrdinalIgnoreCase));
            var selected = complete ?? Enum.GetNames(type).FirstOrDefault()
                ?? throw Failure($"Enum '{type}' has no values.");
            return $"{TypeName(type)}.{selected}";
        }
        if (type == typeof(string))
        {
            var property = typeof(InteractionEvent).GetProperties()
                .FirstOrDefault(candidate => candidate.PropertyType == typeof(string) &&
                    candidate.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase));
            if (property is not null) return $"group.First().{property.Name}";
        }
        throw Failure($"Cannot map DailyListingMetrics factory parameter '{parameter.Name}' of type '{type}'.");
    }
}

static bool ReturnsInteractionCollection(MethodInfo method, Type interactionType)
{
    var result = UnwrapAwaitable(method.ReturnType);
    return result is not null && IsInteractionCollection(result, interactionType);
}

static bool IsInteractionCollection(Type type, Type interactionType)
{
    if (type.IsArray) return type.GetElementType() == interactionType;
    if (type.IsGenericType && type.GetGenericArguments().Length == 1)
    {
        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(IEnumerable<>) || definition == typeof(IReadOnlyList<>) ||
            definition == typeof(IReadOnlyCollection<>) || definition == typeof(IList<>) ||
            definition == typeof(List<>))
        {
            return type.GetGenericArguments()[0] == interactionType;
        }
    }
    return type.GetInterfaces().Any(candidate =>
        candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>) &&
        candidate.GetGenericArguments()[0] == interactionType);
}

static bool IsAwaitable(Type type) => type == typeof(Task) || type == typeof(ValueTask) ||
    type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Task<>) ||
        type.GetGenericTypeDefinition() == typeof(ValueTask<>));

static Type? UnwrapAwaitable(Type type) => type.IsGenericType &&
    (type.GetGenericTypeDefinition() == typeof(Task<>) || type.GetGenericTypeDefinition() == typeof(ValueTask<>))
        ? type.GetGenericArguments()[0]
        : null;

static int ScoreSource(MethodInfo method) =>
    (method.Name.Contains("Unaggregated", StringComparison.OrdinalIgnoreCase) ? 100 : 0) +
    (method.Name.Contains("Event", StringComparison.OrdinalIgnoreCase) ? 20 : 0) +
    (method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)) ? 5 : 0);

static int ScoreSink(MethodInfo method) =>
    (method.Name.Contains("Save", StringComparison.OrdinalIgnoreCase) ? 50 : 0) +
    (method.Name.Contains("Upsert", StringComparison.OrdinalIgnoreCase) ? 40 : 0) +
    (method.Name.Contains("Aggregate", StringComparison.OrdinalIgnoreCase) ? 20 : 0);

static string TypeName(Type type)
{
    if (type.IsArray) return TypeName(type.GetElementType()!) + "[]";
    if (!type.IsGenericType) return "global::" + (type.FullName ?? type.Name).Replace('+', '.');
    var name = type.GetGenericTypeDefinition().FullName
        ?? throw Failure($"Type '{type}' has no full name.");
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

internal sealed record PortMethod(Type Interface, MethodInfo Method);
