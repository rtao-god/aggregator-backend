using Aggregator.Ingestion.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Ingestion.Infrastructure;

public static class IngestionInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddIngestionInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Ingestion")
            ?? throw new InvalidOperationException("Connection string 'Ingestion' is required.");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Ingestion' cannot be empty.");
        }

        services.AddDbContext<IngestionDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<IIngestionBatchRepository, EfIngestionRepository>();
        services.AddScoped<IIngestionProducerRegistry, EfIngestionProducerRegistry>();
        services.AddScoped<
            ICatalogIngestionReferenceReader,
            PostgresCatalogIngestionReferenceReader>();
        services.AddScoped<IngestionReadinessProbe>();
        services.AddSingleton<IIngestionClock, SystemIngestionClock>();
        services.AddSingleton<IIngestionIdSource, UuidV7IngestionIdSource>();
        return services;
    }
}

public sealed class SystemIngestionClock : IIngestionClock
{
    public DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow;
}

public sealed class UuidV7IngestionIdSource : IIngestionIdSource
{
    public Guid CreateId() => Guid.CreateVersion7();
}

/// <summary>Performs read-only connectivity proof for the Ingestion-owned database.</summary>
public sealed class IngestionReadinessProbe
{
    private readonly IngestionDbContext _dbContext;

    public IngestionReadinessProbe(IngestionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
        _dbContext.Database.CanConnectAsync(cancellationToken);
}
