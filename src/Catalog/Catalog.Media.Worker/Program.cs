using Aggregator.CatalogMedia.Application;
using Aggregator.CatalogMedia.Infrastructure;
using Aggregator.CatalogMedia.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Messaging;
using Platform.ObjectStorage;
using Platform.Observability;

var builder = Host.CreateApplicationBuilder(args);
var options = CatalogMediaWorkerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddCatalogMediaApplication();
AddObjectStore(builder);
builder.Services.AddCatalogMediaInfrastructure(builder.Configuration);
builder.Services.AddSingleton<ICatalogMediaScanner, ClamAvCatalogMediaScanner>();
builder.Services.AddSingleton<ICatalogMediaVariantProcessor, ImageMagickCatalogMediaVariantProcessor>();
builder.Services.AddSingleton(options.CreatePublisherOptions());
builder.Services.AddSingleton(options.CreateOutboxOptions());
builder.Services.AddSingleton<RabbitMqEventPublisher>();
builder.Services.AddSingleton<IIntegrationEventPublisher>(services =>
    services.GetRequiredService<RabbitMqEventPublisher>());
builder.Services.AddSingleton<PostgresOutboxDispatcher>();
builder.Services.AddHostedService<CatalogMediaOwnerWorker>();
builder.Services.AddPlatformObservability(builder.Configuration, "catalog-media-worker");
await builder.Build().RunAsync();

static void AddObjectStore(HostApplicationBuilder builder)
{
    var objectOptions = new S3ObjectStoreOptions
    {
        ServiceUrl = new Uri(Require(builder.Configuration, "CatalogMedia:ObjectStorage:ServiceUrl"), UriKind.Absolute),
        Region = builder.Configuration["CatalogMedia:ObjectStorage:Region"] ?? "us-east-1",
        Bucket = Require(builder.Configuration, "CatalogMedia:ObjectStorage:Bucket"),
        AccessKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:AccessKey"),
        SecretKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:SecretKey"),
        ForcePathStyle = bool.TryParse(builder.Configuration["CatalogMedia:ObjectStorage:ForcePathStyle"], out var force)
            ? force : true,
    };
    objectOptions.Validate();
    builder.Services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(objectOptions));
}

static string Require(IConfiguration configuration, string path) =>
    configuration[path] is { Length: > 0 } value ? value.Trim()
        : throw new InvalidOperationException($"Configuration value '{path}' is required.");
