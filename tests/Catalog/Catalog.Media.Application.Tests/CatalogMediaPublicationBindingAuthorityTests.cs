using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Contracts;
using Aggregator.Catalog.Media.Domain;

namespace Catalog.Media.Application.Tests;

public sealed class CatalogMediaPublicationBindingAuthorityTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 5, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcceptedExactVariantProducesOwnerBinding()
    {
        var asset = CreateAcceptedAsset();
        var authority = new CatalogMediaPublicationBindingAuthority(new StubRepository(asset));
        var variant = Assert.Single(
            asset.Variants,
            item => item.Kind == CatalogMediaVariantKind.Original);

        var binding = await authority.RequirePublishableBindingAsync(
            asset.CatalogKey,
            asset.Id,
            asset.AggregateRevision,
            variant.Id,
            CancellationToken.None);

        Assert.Equal(asset.Id, binding.MediaId);
        Assert.Equal(asset.AggregateRevision, binding.MediaAggregateRevision);
        Assert.Equal(variant.Id, binding.VariantId);
        Assert.Equal(variant.ContentType, binding.ContentType);
        Assert.Equal(variant.ContentDigest, binding.ContentDigest);
        Assert.Equal(CatalogMediaPublicationRightsBasisContract.OwnerProvided, binding.RightsBasis);
        Assert.Equal(
            $"urn:aggregator:catalog-media:{asset.Id:N}:{variant.Id:N}:{variant.ContentDigest}",
            binding.ObjectUri);
    }

    [Fact]
    public async Task StaleRevisionFailsClosed()
    {
        var asset = CreateAcceptedAsset();
        var authority = new CatalogMediaPublicationBindingAuthority(new StubRepository(asset));
        var variant = Assert.Single(
            asset.Variants,
            item => item.Kind == CatalogMediaVariantKind.Original);

        var exception = await Assert.ThrowsAsync<CatalogMediaApplicationException>(() =>
            authority.RequirePublishableBindingAsync(
                asset.CatalogKey,
                asset.Id,
                asset.AggregateRevision - 1,
                variant.Id,
                CancellationToken.None));

        Assert.Equal("CATALOG_MEDIA_BINDING_REVISION_CONFLICT", exception.Code);
        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task ForeignCatalogFailsClosed()
    {
        var asset = CreateAcceptedAsset();
        var authority = new CatalogMediaPublicationBindingAuthority(new StubRepository(asset));
        var variant = Assert.Single(
            asset.Variants,
            item => item.Kind == CatalogMediaVariantKind.Original);

        var exception = await Assert.ThrowsAsync<CatalogMediaApplicationException>(() =>
            authority.RequirePublishableBindingAsync(
                "foreign-catalog",
                asset.Id,
                asset.AggregateRevision,
                variant.Id,
                CancellationToken.None));

        Assert.Equal("CATALOG_MEDIA_BINDING_CATALOG_MISMATCH", exception.Code);
        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public async Task MissingVariantFailsClosed()
    {
        var asset = CreateAcceptedAsset();
        var authority = new CatalogMediaPublicationBindingAuthority(new StubRepository(asset));

        var exception = await Assert.ThrowsAsync<CatalogMediaApplicationException>(() =>
            authority.RequirePublishableBindingAsync(
                asset.CatalogKey,
                asset.Id,
                asset.AggregateRevision,
                Guid.Parse("01990000-0000-7000-8000-000000000099"),
                CancellationToken.None));

        Assert.Equal("CATALOG_MEDIA_BINDING_VARIANT_NOT_FOUND", exception.Code);
        Assert.Equal(404, exception.StatusCode);
    }

    private static CatalogMediaAsset CreateAcceptedAsset()
    {
        var assetId = Guid.Parse("01990000-0000-7000-8000-000000000001");
        const string quarantineObjectKey =
            "catalog-media/quarantine/berlin-recording-services/asset-001";
        var asset = CatalogMediaAsset.Register(
            assetId,
            "berlin-recording-services",
            quarantineObjectKey,
            "image/webp",
            new string('a', 64),
            2048,
            CatalogMediaRightsBasis.OwnerProvided,
            "catalog/private-rights/evidence-001",
            Timestamp);
        asset.AuthorizeUpload(
            asset.AggregateRevision,
            Timestamp.AddMinutes(1),
            Timestamp.AddMinutes(16));
        asset.ConfirmUploaded(
            asset.AggregateRevision,
            quarantineObjectKey,
            "image/webp",
            new string('a', 64),
            2048,
            Timestamp.AddMinutes(2));
        asset.StartScan(
            asset.AggregateRevision,
            Timestamp.AddMinutes(3));
        asset.Accept(
            asset.AggregateRevision,
            [
                CatalogMediaVariant.Create(
                    Guid.Parse("01990000-0000-7000-8000-000000000003"),
                    asset.Id,
                    CatalogMediaVariantKind.Original,
                    "catalog-media/published/asset-001/original.webp",
                    "image/webp",
                    new string('b', 64),
                    1800,
                    1200,
                    800,
                    Timestamp.AddMinutes(4)),
                CatalogMediaVariant.Create(
                    Guid.Parse("01990000-0000-7000-8000-000000000004"),
                    asset.Id,
                    CatalogMediaVariantKind.Thumbnail,
                    "catalog-media/published/asset-001/thumbnail.webp",
                    "image/webp",
                    new string('c', 64),
                    512,
                    320,
                    213,
                    Timestamp.AddMinutes(4)),
            ],
            Timestamp.AddMinutes(4));
        return asset;
    }

    private sealed class StubRepository(CatalogMediaAsset asset) : ICatalogMediaRepository
    {
        public Task<CatalogMediaAsset?> GetAsync(Guid assetId, CancellationToken cancellationToken) =>
            Task.FromResult<CatalogMediaAsset?>(assetId == asset.Id ? asset : null);

        public Task<CatalogMediaCommandResult?> ReadCommandResultAsync(
            CatalogMediaCommandIdentity commandIdentity,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogMediaCommandResult> AddAsync(
            CatalogMediaAsset value,
            CatalogMediaCommandIdentity commandIdentity,
            CatalogMediaCommandContext context,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogMediaCommandResult> SaveAsync(
            CatalogMediaAsset value,
            long expectedStoredAggregateRevision,
            CatalogMediaCommandIdentity commandIdentity,
            CatalogMediaCommandContext context,
            CatalogMediaOutboxMessage? outbox,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CatalogMediaProcessingLease?> TryLeaseUploadedAsync(
            string workerIdentity,
            DateTimeOffset nowUtc,
            TimeSpan leaseDuration,
            int maximumAttempts,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CompleteProcessingAsync(
            CatalogMediaProcessingLease lease,
            CatalogMediaAsset value,
            CatalogMediaOutboxMessage outbox,
            DateTimeOffset completedAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> RecordProcessingFailureAsync(
            CatalogMediaProcessingLease lease,
            string failure,
            bool terminal,
            int maximumAttempts,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
