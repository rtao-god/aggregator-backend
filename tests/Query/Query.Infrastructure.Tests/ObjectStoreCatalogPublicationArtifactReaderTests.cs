using System.Security.Cryptography;
using System.Text;
using Aggregator.Query.Application;
using Aggregator.Query.Infrastructure;
using Platform.ObjectStorage;

namespace Query.Infrastructure.Tests;

public sealed class ObjectStoreCatalogPublicationArtifactReaderTests
{
    [Fact]
    public async Task ExactArtifactContractIsReadThroughVerifiedObjectPort()
    {
        var bytes = Encoding.UTF8.GetBytes(ValidArtifactJson());
        var digest = Sha256(bytes);
        var objectStore = new StubObjectStore(bytes, digest);
        var reader = new ObjectStoreCatalogPublicationArtifactReader(
            objectStore,
            new QueryPublicationArtifactReaderOptions());

        var artifact = await reader.ReadAsync(
            "catalog/berlin-recording-services/publications/example.json",
            digest,
            CancellationToken.None);

        Assert.Equal("berlin-recording-services", artifact.CatalogKey);
        Assert.Equal("de-DE", artifact.DefaultLocale);
        Assert.Equal(1, objectStore.ReadCount);
    }

    [Fact]
    public async Task UnknownArtifactPropertyFailsClosed()
    {
        var invalidJson = ValidArtifactJson().Replace(
            "\"listings\":[]",
            "\"listings\":[],\"unexpected\":true",
            StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(invalidJson);
        var digest = Sha256(bytes);
        var reader = new ObjectStoreCatalogPublicationArtifactReader(
            new StubObjectStore(bytes, digest),
            new QueryPublicationArtifactReaderOptions());

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() => reader.ReadAsync(
            "catalog/berlin-recording-services/publications/example.json",
            digest,
            CancellationToken.None));

        Assert.Equal("QUERY_PUBLICATION_ARTIFACT_INVALID", exception.Code);
    }

    [Fact]
    public async Task ObjectKeyOutsideCatalogPrefixFailsBeforeStorageRead()
    {
        var bytes = Encoding.UTF8.GetBytes(ValidArtifactJson());
        var digest = Sha256(bytes);
        var objectStore = new StubObjectStore(bytes, digest);
        var reader = new ObjectStoreCatalogPublicationArtifactReader(
            objectStore,
            new QueryPublicationArtifactReaderOptions());

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() => reader.ReadAsync(
            "ingestion/sealed/example.json",
            digest,
            CancellationToken.None));

        Assert.Equal("QUERY_PUBLICATION_ARTIFACT_KEY_INVALID", exception.Code);
        Assert.Equal(0, objectStore.ReadCount);
    }

    [Fact]
    public async Task ArtifactAboveConfiguredLimitFailsClosed()
    {
        var bytes = Encoding.UTF8.GetBytes(ValidArtifactJson());
        var digest = Sha256(bytes);
        var reader = new ObjectStoreCatalogPublicationArtifactReader(
            new StubObjectStore(bytes, digest),
            new QueryPublicationArtifactReaderOptions
            {
                MaximumArtifactBytes = 16,
            });

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() => reader.ReadAsync(
            "catalog/berlin-recording-services/publications/example.json",
            digest,
            CancellationToken.None));

        Assert.Equal("QUERY_PUBLICATION_ARTIFACT_TOO_LARGE", exception.Code);
    }

    private static string ValidArtifactJson() => $$"""
        {
          "contractIdentity":"aggregator-catalog-publication",
          "contractRevision":3,
          "publicationId":"{{Guid.Parse("0198a400-0000-7000-8000-000000000001")}}",
          "catalogKey":"berlin-recording-services",
          "defaultLocale":"de-DE",
          "supportedLocales":["de-DE","en-GB"],
          "configurationRevisionId":"{{Guid.Parse("0198a400-0000-7000-8000-000000000002")}}",
          "publicationSequence":1,
          "createdAtUtc":"2026-08-04T00:00:00+00:00",
          "listings":[]
        }
        """;

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class StubObjectStore(byte[] content, string expectedDigest) : IObjectStore
    {
        public int ReadCount { get; private set; }

        public Task<StoredObjectDescriptor> PutVerifiedAsync(
            string key,
            Stream content,
            long expectedSize,
            string expectedSha256,
            string contentType,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Write is outside this reader contract test.");

        public Task<StoredObjectDescriptor> HeadAsync(
            string key,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Head is outside this reader contract test.");

        public Task<Stream> OpenReadVerifiedAsync(
            string key,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(expectedDigest, expectedSha256);
            ReadCount++;
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        public Task<ScopedObjectUrl> CreateScopedReadUrlAsync(
            string key,
            TimeSpan lifetime,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Scoped reads are outside this reader contract test.");

        public Task<ScopedObjectUrl> CreateScopedWriteUrlAsync(
            string key,
            string contentType,
            TimeSpan lifetime,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Scoped writes are outside this reader contract test.");

        public Task DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Delete is outside this reader contract test.");
    }
}
