using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed record CatalogReadinessResult(bool Ready, string State, string? FailureType);

/// <summary>Reads Catalog database availability without migrating or repairing state.</summary>
public sealed class CatalogReadinessProbe(CatalogDbContext dbContext)
{
    public async Task<CatalogReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var ready = await dbContext.Database.CanConnectAsync(cancellationToken);
            return ready
                ? new CatalogReadinessResult(true, "ready", null)
                : new CatalogReadinessResult(false, "database_unavailable", null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CatalogReadinessResult(false, "database_unavailable", exception.GetType().Name);
        }
    }
}
