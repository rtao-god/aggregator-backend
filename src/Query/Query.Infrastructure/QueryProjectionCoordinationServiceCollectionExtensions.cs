using Aggregator.Query.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aggregator.Query.Infrastructure;

public static class QueryProjectionCoordinationServiceCollectionExtensions
{
    /// <summary>
    /// Replaces direct projection stores with catalog-serialized decorators and the publication
    /// overlay-preserving owner path used by the Query worker composition root.
    /// </summary>
    public static IServiceCollection AddQueryProjectionCoordination(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.RemoveAll<IQueryProjectionStore>();
        services.RemoveAll<IPromotionPlacementProjectionStore>();
        services.RemoveAll<IVisibilitySafetyProjectionStore>();

        services.AddScoped<NpgsqlQueryProjectionStore>();
        services.AddScoped<IQueryProjectionStore, OverlayPreservingQueryProjectionStore>();

        services.AddScoped<PostgresPromotionOverlayProjectionStore>();
        services.AddScoped<
            IPromotionPlacementProjectionStore,
            CoordinatedPromotionPlacementProjectionStore>();

        services.AddScoped<PostgresVisibilitySafetyProjectionStore>();
        services.AddScoped<
            IVisibilitySafetyProjectionStore,
            CoordinatedVisibilitySafetyProjectionStore>();
        return services;
    }
}
