using Aggregator.Catalog.Contracts;

namespace Aggregator.Catalog.Application;

/// <summary>
/// Builds the single Catalog-owned import artifact from an authored product configuration.
/// </summary>
public static class CatalogProductConfigurationArtifactBuilder
{
    /// <summary>
    /// Validates the complete configuration through Catalog domain invariants and requires its authored SHA-256 identity to match the canonical payload.
    /// </summary>
    public static ImportProductConfigurationRequest BuildImportRequest(
        ProductConfigurationContract configuration,
        string expectedContentDigest)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var normalizedExpectedDigest = RequireSha256(
            expectedContentDigest,
            nameof(expectedContentDigest));
        var canonicalDocument = CatalogCanonicalJson.SerializeConfiguration(configuration);
        var actualContentDigest = CatalogCanonicalJson.ComputeSha256(canonicalDocument);

        // Domain construction is the semantic cross-file validation boundary used by runtime import.
        _ = CatalogContractMapper.ToDomain(configuration, actualContentDigest);

        if (!string.Equals(
                normalizedExpectedDigest,
                actualContentDigest,
                StringComparison.Ordinal))
        {
            throw new CatalogContractException(
                "catalog.product_configuration_digest_mismatch",
                $"Product configuration expected content digest '{normalizedExpectedDigest}' but canonical content produced '{actualContentDigest}'.");
        }

        return new ImportProductConfigurationRequest(
            CatalogContractIdentity.ProductConfiguration,
            CatalogContractIdentity.ProductConfigurationRevision,
            actualContentDigest,
            configuration);
    }

    private static string RequireSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Expected content digest must be a lowercase SHA-256 hexadecimal string.",
                parameterName);
        }

        return value;
    }
}
