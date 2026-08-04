using System.Security.Cryptography;
using Aggregator.Ingestion.Application;

namespace Ingestion.Application.Tests;

public sealed class IngestionDocumentDigestTests
{
    [Fact]
    public void DigestIsComputedFromCanonicalBytesWithoutReserialization()
    {
        byte[] document = [1, 2, 3, 4, 5];
        var expected = Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();

        var actual = IngestionDocumentDigest.Compute(document);

        Assert.Equal(expected, actual);
    }
}
