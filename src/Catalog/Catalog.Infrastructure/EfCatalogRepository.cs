using System.Data;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository :
    ICatalogRepository,
    ICatalogPublicationOperationCommitter,
    ICatalogConfigurationActivationRepository,
    ICatalogListingDisputeRepository
{
    private readonly CatalogDbContext _dbContext;
    private readonly ICatalogPublicationArtifactStore _publicationArtifactStore;

    public EfCatalogRepository(
        CatalogDbContext dbContext,
        ICatalogPublicationArtifactStore publicationArtifactStore)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _publicationArtifactStore = publicationArtifactStore
            ?? throw new ArgumentNullException(nameof(publicationArtifactStore));
    }

    private async Task ExecuteInTransactionAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        try
        {
            await operation(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
