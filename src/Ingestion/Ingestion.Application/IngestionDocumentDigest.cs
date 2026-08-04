using System.Security.Cryptography;

namespace Aggregator.Ingestion.Application;

/// <summary>Computes the digest of already-canonical serialized bytes without serializing them again.</summary>
public static class IngestionDocumentDigest
{
    public static string Compute(ReadOnlySpan<byte> canonicalDocument)
    {
        if (canonicalDocument.IsEmpty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Serialization",
                "INGESTION_DOCUMENT_EMPTY",
                500,
                "A canonical Ingestion document cannot be empty when computing its digest.",
                "Correct the canonical document producer before persistence or verification.");
        }

        return Convert.ToHexString(SHA256.HashData(canonicalDocument)).ToLowerInvariant();
    }
}
