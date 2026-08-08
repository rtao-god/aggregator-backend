using Aggregator.Catalog.Contracts;

namespace Aggregator.Catalog.Application;

/// <summary>
/// Builds the single Catalog-owned import artifact from an authored product configuration.
/// </summary>
public static class CatalogProductConfigurationArtifactBuilder
{
    /// <summary>
    /// Validates the complete configuration through Catalog domain invariants and seals its canonical SHA-256 digest.
    /// </summary>
    public static ImportProductConfigurationRequest BuildImportRequest(
        ProductConfigurationContract configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var canonicalDocument = CatalogCanonicalJson.SerializeConfiguration(configuration);
        var contentDigest = CatalogCanonicalJson.ComputeSha256(canonicalDocument);

        // Domain construction is the semantic cross-file validation boundary used by runtime import.
        _ = CatalogContractMapper.ToDomain(configuration, contentDigest);

        return new ImportProductConfigurationRequest(
            CatalogContractIdentity.ProductConfiguration,
            CatalogContractIdentity.ProductConfigurationRevision,
            contentDigest,
            configuration);
    }
}
