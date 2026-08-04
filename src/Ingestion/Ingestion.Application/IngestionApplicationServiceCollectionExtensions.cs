using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Application;

public static class IngestionApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddIngestionApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<RegisterIngestionBatchService>();
        services.AddScoped<ReadIngestionBatchService>();
        services.AddScoped<PrepareIngestionUploadService>();
        services.AddScoped<CompleteIngestionUploadService>();
        return services;
    }
}
