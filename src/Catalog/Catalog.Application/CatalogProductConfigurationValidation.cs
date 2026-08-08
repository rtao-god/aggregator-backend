using System.Security.Cryptography;
using System.Text.Json;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Identifies the immutable Catalog product-configuration validation result contract.</summary>
public static class CatalogProductConfigurationValidationContract
{
    public const string Identity = "aggregator-catalog-product-configuration-validation";
    public const int Revision = 1;
}

/// <summary>Describes the exact owner validation result sealed beside one Catalog configuration revision.</summary>
public sealed record CatalogProductConfigurationValidationProof(
    string ContractIdentity,
    int ContractRevision,
    Guid ConfigurationRevisionId,
    string ContentDigest,
    ProductConfigurationValidationState State,
    string ResultDigest);

public enum ProductConfigurationValidationState
{
    Validated = 1,
}

/// <summary>Creates and verifies the single Catalog-owned validation proof for canonical configuration bytes.</summary>
public static class CatalogProductConfigurationValidation
{
    public static CatalogProductConfigurationValidationProof Create(
        ProductConfiguration configuration,
        ReadOnlyMemory<byte> canonicalDocument)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (canonicalDocument.IsEmpty)
        {
            throw new ArgumentException(
                "Canonical configuration document cannot be empty.",
                nameof(canonicalDocument));
        }

        var actualContentDigest = Convert
            .ToHexString(SHA256.HashData(canonicalDocument.Span))
            .ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(configuration.Digest),
                Convert.FromHexString(actualContentDigest)))
        {
            throw new CatalogContractException(
                "catalog.configuration_validation_content_mismatch",
                $"Configuration revision '{configuration.RevisionId}' expected canonical content digest '{configuration.Digest}' but received '{actualContentDigest}'.");
        }

        var resultDigest = ComputeResultDigest(
            configuration.RevisionId,
            actualContentDigest,
            ProductConfigurationValidationState.Validated);
        return new CatalogProductConfigurationValidationProof(
            CatalogProductConfigurationValidationContract.Identity,
            CatalogProductConfigurationValidationContract.Revision,
            configuration.RevisionId,
            actualContentDigest,
            ProductConfigurationValidationState.Validated,
            resultDigest);
    }

    private static string ComputeResultDigest(
        Guid configurationRevisionId,
        string contentDigest,
        ProductConfigurationValidationState state)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(
                "contractIdentity",
                CatalogProductConfigurationValidationContract.Identity);
            writer.WriteNumber(
                "contractRevision",
                CatalogProductConfigurationValidationContract.Revision);
            writer.WriteString(
                "configurationRevisionId",
                configurationRevisionId);
            writer.WriteString("contentDigest", contentDigest);
            writer.WriteString("state", "validated");
            writer.WriteEndObject();
        }

        return Convert
            .ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))))
            .ToLowerInvariant();
    }
}
