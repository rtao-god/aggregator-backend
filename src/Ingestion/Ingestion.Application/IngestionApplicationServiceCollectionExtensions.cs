using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aggregator.Ingestion.Application;

public static class IngestionApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddIngestionApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<RegisterIngestionBatchService>();
        services.AddScoped<ReadIngestionBatchService>();
        services.AddIngestionProcessingApplication();
        services.RemoveAll<DeliverIngestionCatalogCommandsService>();
        services.AddScoped<ProcessIngestionCatalogDeliveriesService>();
        services.AddScoped<PrepareIngestionUploadService>();
        services.AddScoped<CompleteIngestionUploadService>();
        return services;
    }
}
