using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public static class CatalogPersistenceRehydration
{
    public static ProductConfiguration Configuration(
        ProductConfigurationContract contract,
        string contentDigest) =>
        CatalogContractMapper.ToDomain(contract, contentDigest);
}
