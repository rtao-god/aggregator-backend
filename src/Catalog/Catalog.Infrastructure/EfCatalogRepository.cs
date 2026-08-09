using Aggregator.Catalog.Application;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository :
    ICatalogRepository,
    ICatalogConfigurationActivationRepository,
    ICatalogPublicationOperationCommitter
{
    private readonly CatalogDbContext _dbContext;

    public EfCatalogRepository(CatalogDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    private async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        await action(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
