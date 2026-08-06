using Aggregator.Catalog.Contracts;
using Aggregator.Promotion.Contracts;
using Aggregator.Query.Application;
using Aggregator.Query.Infrastructure;
using Aggregator.Query.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.ObjectStorage;
using Platform.Observability;

var builder = Host.CreateApplicationBuilder(args);
var queryConnectionString = RequireConfiguration(
    builder.Configuration.GetConnectionString("Query"),
    "ConnectionStrings:Query");
var brokerUri = new Uri(
    RequireConfiguration(
        builder.Configuration[$"{QueryWorkerOptions.SectionName}:BrokerUri"],
        $"{QueryWorkerOptions.SectionName}:BrokerUri"),
    UriKind.Absolute);
var workerOptions = new QueryWorkerOptions
{
    BrokerUri = brokerUri,
    Exchange = builder.Configuration[$"{QueryWorkerOptions.SectionName}:Exchange"]
        ?? "aggregator.events",
    Queue = builder.Configuration[$"{QueryWorkerOptions.SectionName}:Queue"]
        ?? "query.catalog-publication-projection",
    RoutingKey = builder.Configuration[$"{QueryWorkerOptions.SectionName}:RoutingKey"]
        ?? CatalogIntegrationEventTypes.PublicationActivated,
    PrefetchCount = ParseUShort(
        builder.Configuration[$"{QueryWorkerOptions.SectionName}:PrefetchCount"],
        8,
        $"{QueryWorkerOptions.SectionName}:PrefetchCount"),
};
workerOptions.Validate();
var promotionBrokerUriValue = builder.Configuration[
    $"{QueryPromotionWorkerOptions.SectionName}:BrokerUri"];
var promotionWorkerOptions = new QueryPromotionWorkerOptions
{
    BrokerUri = promotionBrokerUriValue is null
        ? workerOptions.BrokerUri
        : new Uri(
            RequireConfiguration(
                promotionBrokerUriValue,
                $"{QueryPromotionWorkerOptions.SectionName}:BrokerUri"),
            UriKind.Absolute),
    Exchange = builder.Configuration[$"{QueryPromotionWorkerOptions.SectionName}:Exchange"]
        ?? workerOptions.Exchange,
    Queue = builder.Configuration[$"{QueryPromotionWorkerOptions.SectionName}:Queue"]
        ?? "query.promotion-placement-projection",
    DeadLetterExchange = builder.Configuration[
        $"{QueryPromotionWorkerOptions.SectionName}:DeadLetterExchange"]
        ?? "aggregator.dead-letter",
    DeadLetterQueue = builder.Configuration[
        $"{QueryPromotionWorkerOptions.SectionName}:DeadLetterQueue"]
        ?? "query.promotion-placement-projection.dead-letter",
    RoutingKey = builder.Configuration[$"{QueryPromotionWorkerOptions.SectionName}:RoutingKey"]
        ?? PromotionIntegrationEventTypes.PlacementChanged,
    PrefetchCount = ParseUShort(
        builder.Configuration[$"{QueryPromotionWorkerOptions.SectionName}:PrefetchCount"],
        8,
        $"{QueryPromotionWorkerOptions.SectionName}:PrefetchCount"),
    DeliveryLimit = ParseInt(
        builder.Configuration[$"{QueryPromotionWorkerOptions.SectionName}:DeliveryLimit"],
        8,
        $"{QueryPromotionWorkerOptions.SectionName}:DeliveryLimit"),
    RetryDelay = TimeSpan.FromMilliseconds(ParseInt(
        builder.Configuration[$"{QueryPromotionWorkerOptions.SectionName}:RetryDelayMilliseconds"],
        500,
        $"{QueryPromotionWorkerOptions.SectionName}:RetryDelayMilliseconds")),
};
promotionWorkerOptions.Validate();
var visibilityBrokerUriValue = builder.Configuration[
    $"{QueryVisibilityWorkerOptions.SectionName}:BrokerUri"];
var visibilityWorkerOptions = new QueryVisibilityWorkerOptions
{
    BrokerUri = visibilityBrokerUriValue is null
        ? workerOptions.BrokerUri
        : new Uri(
            RequireConfiguration(
                visibilityBrokerUriValue,
                $"{QueryVisibilityWorkerOptions.SectionName}:BrokerUri"),
            UriKind.Absolute),
    Exchange = builder.Configuration[$"{QueryVisibilityWorkerOptions.SectionName}:Exchange"]
        ?? workerOptions.Exchange,
    Queue = builder.Configuration[$"{QueryVisibilityWorkerOptions.SectionName}:Queue"]
        ?? "query.catalog-visibility-safety",
    DeadLetterExchange = builder.Configuration[
        $"{QueryVisibilityWorkerOptions.SectionName}:DeadLetterExchange"]
        ?? "aggregator.dead-letter",
    DeadLetterQueue = builder.Configuration[
        $"{QueryVisibilityWorkerOptions.SectionName}:DeadLetterQueue"]
        ?? "query.catalog-visibility-safety.dead-letter",
    RoutingKey = builder.Configuration[$"{QueryVisibilityWorkerOptions.SectionName}:RoutingKey"]
        ?? CatalogIntegrationEventTypes.PublicVisibilitySuppressionChanged,
    PrefetchCount = ParseUShort(
        builder.Configuration[$"{QueryVisibilityWorkerOptions.SectionName}:PrefetchCount"],
        4,
        $"{QueryVisibilityWorkerOptions.SectionName}:PrefetchCount"),
    DeliveryLimit = ParseInt(
        builder.Configuration[$"{QueryVisibilityWorkerOptions.SectionName}:DeliveryLimit"],
        8,
        $"{QueryVisibilityWorkerOptions.SectionName}:DeliveryLimit"),
    RetryDelay = TimeSpan.FromMilliseconds(ParseInt(
        builder.Configuration[$"{QueryVisibilityWorkerOptions.SectionName}:RetryDelayMilliseconds"],
        500,
        $"{QueryVisibilityWorkerOptions.SectionName}:RetryDelayMilliseconds")),
};
visibilityWorkerOptions.Validate();
var objectStoreOptions = new S3ObjectStoreOptions
{
    ServiceUrl = new Uri(
        RequireConfiguration(
            builder.Configuration["Query:ObjectStorage:ServiceUrl"],
            "Query:ObjectStorage:ServiceUrl"),
        UriKind.Absolute),
    Region = builder.Configuration["Query:ObjectStorage:Region"] ?? "us-east-1",
    Bucket = RequireConfiguration(
        builder.Configuration["Query:ObjectStorage:Bucket"],
        "Query:ObjectStorage:Bucket"),
    AccessKey = RequireConfiguration(
        builder.Configuration["Query:ObjectStorage:AccessKey"],
        "Query:ObjectStorage:AccessKey"),
    SecretKey = RequireConfiguration(
        builder.Configuration["Query:ObjectStorage:SecretKey"],
        "Query:ObjectStorage:SecretKey"),
    ForcePathStyle = ParseBoolean(
        builder.Configuration["Query:ObjectStorage:ForcePathStyle"],
        defaultValue: true,
        "Query:ObjectStorage:ForcePathStyle"),
};
objectStoreOptions.Validate();

builder.Services
    .AddQueryApplication()
    .AddQueryDatabase(new QueryDatabaseOptions
    {
        ConnectionString = queryConnectionString,
    })
    .AddQueryPromotionOverlayProjection()
    .AddQueryVisibilitySafetyProjection()
    .AddQueryWorker(workerOptions, promotionWorkerOptions, visibilityWorkerOptions);
builder.Services.AddPlatformObservability(builder.Configuration, "query-projection-worker");
builder.Services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(objectStoreOptions));
builder.Services.AddSingleton<IQueryClock, SystemQueryClock>();
builder.Services.AddSingleton<IQueryIdFactory, UuidV7QueryIdFactory>();
builder.Services.AddScoped(
    _ => new QueryPublicationArtifactReaderOptions
    {
        AllowedObjectPrefix = builder.Configuration["Query:PublicationArtifact:AllowedObjectPrefix"]
            ?? "catalog/",
        MaximumArtifactBytes = ParseLong(
            builder.Configuration["Query:PublicationArtifact:MaximumArtifactBytes"],
            64L * 1024L * 1024L,
            "Query:PublicationArtifact:MaximumArtifactBytes"),
    });
builder.Services.AddScoped<ICatalogPublicationArtifactReader, ObjectStoreCatalogPublicationArtifactReader>();
var projectionStoreType = typeof(QueryDatabaseOptions).Assembly
    .GetTypes()
    .Single(type =>
        !type.IsAbstract &&
        !type.IsInterface &&
        typeof(IQueryProjectionStore).IsAssignableFrom(type));
builder.Services.AddScoped(typeof(IQueryProjectionStore), projectionStoreType);

await builder.Build().RunAsync();

static string RequireConfiguration(string? value, string path) =>
    !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new InvalidOperationException($"Configuration value '{path}' is required.");

static bool ParseBoolean(string? value, bool defaultValue, string path) =>
    value is null
        ? defaultValue
        : bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration value '{path}' must be a Boolean.");

static ushort ParseUShort(string? value, ushort defaultValue, string path) =>
    value is null
        ? defaultValue
        : ushort.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration value '{path}' must be an unsigned integer.");

static int ParseInt(string? value, int defaultValue, string path) =>
    value is null
        ? defaultValue
        : int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration value '{path}' must be an integer.");

static long ParseLong(string? value, long defaultValue, string path) =>
    value is null
        ? defaultValue
        : long.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration value '{path}' must be an integer.");

internal sealed class SystemQueryClock : IQueryClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

internal sealed class UuidV7QueryIdFactory : IQueryIdFactory
{
    public Guid Create() => Guid.CreateVersion7();
}
