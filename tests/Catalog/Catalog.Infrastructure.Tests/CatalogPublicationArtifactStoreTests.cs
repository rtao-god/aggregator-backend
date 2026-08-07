using System.Globalization;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Infrastructure;
using Amazon.S3.Model;

namespace Catalog.Infrastructure.Tests;

public sealed class CatalogPublicationArtifactStoreTests
{
    private const string Digest = "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a";
    private const string ObjectKey = "catalog/test/publications/0192f5f0000070008000000000000001.json";

    [Fact]
    public void CurrentPublicationContractMetadataIsAccepted()
    {
        var metadata = CreateMetadata(
            CatalogPublicationArtifactContract.Identity,
            CatalogPublicationArtifactContract.Revision.ToString(CultureInfo.InvariantCulture));

        S3CatalogPublicationArtifactStore.EnsureMetadata(
            metadata,
            ObjectKey,
            expectedLength: 2,
            Digest);
    }

    [Fact]
    public void StalePublicationContractRevisionIsRejected()
    {
        var metadata = CreateMetadata(
            CatalogPublicationArtifactContract.Identity,
            (CatalogPublicationArtifactContract.Revision - 1).ToString(CultureInfo.InvariantCulture));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            S3CatalogPublicationArtifactStore.EnsureMetadata(
                metadata,
                ObjectKey,
                expectedLength: 2,
                Digest));

        Assert.Contains("contract revision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignPublicationContractIdentityIsRejected()
    {
        var metadata = CreateMetadata(
            "foreign-catalog-publication",
            CatalogPublicationArtifactContract.Revision.ToString(CultureInfo.InvariantCulture));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            S3CatalogPublicationArtifactStore.EnsureMetadata(
                metadata,
                ObjectKey,
                expectedLength: 2,
                Digest));

        Assert.Contains("contract identity", exception.Message, StringComparison.Ordinal);
    }

    private static GetObjectMetadataResponse CreateMetadata(
        string contractIdentity,
        string contractRevision)
    {
        var metadata = new GetObjectMetadataResponse
        {
            ContentLength = 2,
        };
        metadata.Metadata["x-amz-meta-sha256"] = Digest;
        metadata.Metadata["x-amz-meta-contract"] = contractIdentity;
        metadata.Metadata["x-amz-meta-contract-revision"] = contractRevision;
        return metadata;
    }
}
