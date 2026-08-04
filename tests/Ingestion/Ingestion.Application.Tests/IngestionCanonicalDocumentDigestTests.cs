using System.Security.Cryptography;
using Aggregator.Ingestion.Application;

namespace Ingestion.Application.Tests;

public sealed class IngestionCanonicalDocumentDigestTests
{
    [Fact]
    public void ByteArrayOverloadHashesCanonicalBytesWithoutJsonReserialization()
    {
        byte[] document = [1, 2, 3, 4, 5];
        var expected = Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();

        var actual = IngestionCanonicalJson.ComputeDigest(document);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EmptyCanonicalDocumentIsRejected()
    {
        var error = Assert.Throws<IngestionApplicationException>(() =>
            IngestionCanonicalJson.ComputeDigest(Array.Empty<byte>()));

        Assert.Equal("INGESTION_DOCUMENT_EMPTY", error.Code);
    }
}
