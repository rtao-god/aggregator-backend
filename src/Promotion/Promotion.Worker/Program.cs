using Aggregator.Promotion.Application;
using Aggregator.Promotion.Infrastructure;
using Aggregator.Promotion.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Messaging;
using Platform.Observability;

var builder = Host.CreateApplicationBuilder(args);
var options = PromotionWorkerOptions.FromConfiguration(builder.Configuration);
var eligibilityOptions =
    PromotionEligibilityProjectionWorkerOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(eligibilityOptions);
builder.Services.AddPromotionApplication();
builder.Services.AddPromotionInfrastructure(builder.Configuration);
builder.Services.AddSingleton(options.CreatePublisherOptions());
builder.Services.AddSingleton(options.CreateOutboxOptions());
builder.Services.AddSingleton<RabbitMqEventPublisher>();
builder.Services.AddSingleton<IIntegrationEventPublisher>(services =>
    services.GetRequiredService<RabbitMqEventPublisher>());
builder.Services.AddSingleton<PostgresOutboxDispatcher>();
builder.Services.AddHostedService<PromotionOwnerWorker>();
builder.Services.AddHostedService<PromotionEligibilityProjectionWorker>();
builder.Services.AddPlatformObservability(builder.Configuration, "promotion-worker");

await builder.Build().RunAsync();
