using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Query.Application;

public static class QueryApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddQueryApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<PromotionOverlayProjectionService>();
        services.AddScoped<QueryProjectionService>();
        return services;
    }
}
