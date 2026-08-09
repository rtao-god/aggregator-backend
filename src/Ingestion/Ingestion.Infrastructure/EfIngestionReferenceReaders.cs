using Aggregator.Ingestion.Application;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Reads only the Ingestion-owned producer authorization registry.</summary>
public sealed class EfIngestionProducerRegistry : IIngestionProducerRegistry
{
    private readonly IngestionDbContext _dbContext;

    public EfIngestionProducerRegistry(IngestionDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    public async Task<RegisteredIngestionProducer?> GetAsync(
        string producerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(producerIdentity);
        var row = await _dbContext.Producers
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Identity == producerIdentity, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (row.SupportedContractRevisions is null ||
            row.SupportedContractRevisions.Length == 0 ||
            row.SupportedContractRevisions.Any(revision => revision <= 0) ||
            row.SupportedContractRevisions.Distinct().Count() != row.SupportedContractRevisions.Length)
        {
            throw new IngestionApplicationException(
                "Ingestion.ProducerRegistry",
                "INGESTION_PRODUCER_REGISTRY_CORRUPT",
                500,
                $"Producer '{row.Identity}' has an invalid supported-contract revision set.",
                "Repair the producer registration through an Ingestion owner command.");
        }

        return new RegisteredIngestionProducer(
            row.Identity,
            row.Active,
            Array.AsReadOnly(
                row.SupportedContractRevisions
                    .Order()
                    .ToArray()));
    }
}
