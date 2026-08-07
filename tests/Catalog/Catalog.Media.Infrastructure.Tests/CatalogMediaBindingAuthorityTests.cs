using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Domain;
using Aggregator.Catalog.Media.Infrastructure;

namespace Catalog.Media.Infrastructure.Tests;

public sealed class CatalogMediaBindingAuthorityTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AcceptedExactVariantProducesOwnerBinding()
    {
        var asset = CreateAcceptedAsset();
        var variant = Assert.Single(asset.Variants, item => item.Kind == CatalogMediaVariantKind.Card);
        var authority = new CatalogMediaBindingAuthority(new StubRepository(asset));

        var binding = await authority.RequirePublishableBindingAsync(
            CatalogKey.Create(asset.CatalogKey),
            asset.Id,
            asset.AggregateRevision,
            variant.Id,
            CancellationToken.None);

        Assert.Equal(asset.Id, binding.MediaId);
        Assert.Equal(asset.AggregateRevision, binding.MediaAggregateRevision);
        Assert.Equal(variant.Id, binding.VariantId);
        Assert.Equal(variant.ContentType, binding.ContentType);
        Assert.Equal(variant.ContentDigest, binding.ContentDigest);
        Assert.Equal(MediaRightsBasis.ExplicitLicense, binding.RightsBasis);
        Assert.Equal(
            $"urn:aggregator:catalog-media:{asset.Id:N}:{variant.Id:N}:{variant.ContentDigest}",
            binding.ObjectUri.AbsoluteUri);
    }

    [Fact]
    public async Task StaleAggregateRevisionIsRejected()
    {
        var asset = CreateAcceptedAsset();
        var variant = Assert.Single(asset.Variants, item => item.Kind == CatalogMediaVariantKind.Card);
        var authority = new CatalogMediaBindingAuthority(new StubRepository(asset));

        var exception = await Assert.ThrowsAsync<CatalogConflictException>(() =>
            authority.RequirePublishableBindingAsync(
                CatalogKey.Create(asset.CatalogKey),
                asset.Id,
                asset.AggregateRevision - 1,
                variant.Id,
                CancellationToken.None));

        Assert.Contains("expected revision", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForeignCatalogIsRejected()
    {
        var asset = CreateAcceptedAsset();
        var variant = Assert.Single(asset.Variants, item => item.Kind == CatalogMediaVariantKind.Card);
        var authority = new CatalogMediaBindingAuthority(new StubRepository(asset));

        var exception = await Assert.ThrowsAsync<CatalogConflictException>(() =>
            authority.RequirePublishableBindingAsync(
                CatalogKey.Create("another-catalog"),
                asset.Id,
                asset.AggregateRevision,
                variant.Id,
                CancellationToken.None));

        Assert.Contains("belongs to catalog", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RightsRevokedAssetIsRejected()
    {
        var asset = CreateAcceptedAsset();
        var variant = Assert.Single(asset.Variants, item => item.Kind == CatalogMediaVariantKind.Card);
        asset.RevokeRights(
            asset.AggregateRevision,
            Guid.Parse("0198ff00-0000-7000-8000-000000000090"),
            "License was withdrawn.",
            Timestamp.AddMinutes(5));
        var authority = new CatalogMediaBindingAuthority(new StubRepository(asset));

        var exception = await Assert.ThrowsAsync<CatalogConflictException>(() =>
            authority.RequirePublishableBindingAsync(
                CatalogKey.Create(asset.CatalogKey),
                asset.Id,
                asset.AggregateRevision,
                variant.Id,
                CancellationToken.None));

        Assert.Contains("not publishable", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VariantFromAnotherAssetIsNotResolved()
    {
        var asset = CreateAcceptedAsset();
        var authority = new CatalogMediaBindingAuthority(new StubRepository(asset));

        await Assert.ThrowsAsync<CatalogNotFoundException>(() =>
            authority.RequirePublishableBindingAsync(
                CatalogKey.Create(asset.CatalogKey),
                asset.Id,
                asset.AggregateRevision,
                Guid.Parse("0198ff00-0000-7000-8000-000000000099"),
                CancellationToken.None));
    }

    private static CatalogMediaAsset CreateAcceptedAsset()
    {
        var assetId = Guid.Parse("0198ff00-0000-7000-8000-000000000001");
        const string sourceDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var asset = CatalogMediaAsset.Register(
            assetId,
            "berlin-recording-services",
            "catalog-media/quarantine/berlin-recording-services/asset-001",
            "image/jpeg",
            sourceDigest,
            2048,
            CatalogMediaRightsBasis.Licensed,
            "catalog/claims/private/media-license-001",
            Timestamp);
        asset.AuthorizeUpload(asset.AggregateRevision, Timestamp, Timestamp.AddMinutes(10));
        asset.ConfirmUploaded(
            asset.AggregateRevision,
            asset.QuarantineObjectKey,
            asset.ExpectedContentType,
            asset.ExpectedContentDigest,
            asset.ExpectedSize,
            Timestamp.AddMinutes(1));
        asset.StartScan(asset.AggregateRevision, Timestamp.AddMinutes(2));
        asset.Accept(
            asset.AggregateRevision,
            [
                CatalogMediaVariant.Create(
                    Guid.Parse("0198ff00-0000-7000-8000-000000000010"),
                    assetId,
                    CatalogMediaVariantKind.Original,
                    "catalog-media/published/original-001",
                    "image/jpeg",
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    2048,
                    1200,
                    800,
                    Timestamp.AddMinutes(3)),
                CatalogMediaVariant.Create(
                    Guid.Parse("0198ff00-0000-7000-8000-000000000011"),
                    assetId,
                    CatalogMediaVariantKind.Thumbnail,
                    "catalog-media/published/thumbnail-001",
                    "image/webp",
                    "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                    512,
                    320,
                    213,
                    Timestamp.AddMinutes(3)),
                CatalogMediaVariant.Create(
                    Guid.Parse("0198ff00-0000-7000-8000-000000000012"),
                    assetId,
                    CatalogMediaVariantKind.Card,
                    "catalog-media/published/card-001",
                    "image/webp",
                    "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                    1024,
                    768,
                    512,
                    Timestamp.AddMinutes(3)),
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
