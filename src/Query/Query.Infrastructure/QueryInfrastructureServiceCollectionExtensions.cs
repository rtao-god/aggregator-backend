using Aggregator.Query.Application;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

public static class QueryInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddQueryDatabase(
        this IServiceCollection services,
        QueryDatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var dataSource = new NpgsqlDataSourceBuilder(options.ConnectionString).Build();
        services.AddSingleton(options);
        services.AddSingleton(dataSource);
        services.AddSingleton<QueryReadinessProbe>();
        return services;
    }

    public static IServiceCollection AddQueryPublicReadInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IQueryClock, SystemQueryClock>();
        services.AddSingleton<NpgsqlPublicQueryStore>();
        services.AddSingleton<IPublicQueryStore, SafetyAwarePublicQueryStore>();
        services.AddSingleton<PublicQueryService>();
        return services;
    }

    public static IServiceCollection AddQueryProjectionInfrastructure(
        this IServiceCollection services,
        QueryPublicationArtifactReaderOptions artifactReaderOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(artifactReaderOptions);
        artifactReaderOptions.Validate();

        services.AddSingleton(artifactReaderOptions);
        services.AddSingleton<IQueryClock, SystemQueryClock>();
        services.AddSingleton<IQueryIdFactory, UuidV7QueryIdFactory>();
        services.AddSingleton<ICatalogPublicationArtifactReader, ObjectStoreCatalogPublicationArtifactReader>();
        services.AddSingleton<IQueryActivationCheckpointReader, NpgsqlQueryActivationCheckpointReader>();
        services.AddSingleton<IQueryProjectionStore, NpgsqlQueryProjectionStore>();
        services.AddSingleton<QueryProjectionService>();
        return services;
    }

    public static IServiceCollection AddQueryVisibilitySafetyProjection(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<
            IVisibilitySafetyProjectionStore,
            PostgresVisibilitySafetyProjectionStore>();
        return services;
    }
}
