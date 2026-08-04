using Aggregator.Catalog.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Messaging;
using Platform.Observability;

var builder = Host.CreateApplicationBuilder(args);
var options = CatalogWorkerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(options.CreateRabbitMqPublisherOptions());
builder.Services.AddSingleton(options.CreateOutboxDispatcherOptions());
builder.Services.AddSingleton<RabbitMqEventPublisher>();
builder.Services.AddSingleton<IIntegrationEventPublisher>(services =>
    services.GetRequiredService<RabbitMqEventPublisher>());
builder.Services.AddSingleton<PostgresOutboxDispatcher>();
builder.Services.AddHostedService<CatalogOutboxWorker>();
builder.Services.AddPlatformObservability(builder.Configuration, "catalog-worker");

await builder.Build().RunAsync();
