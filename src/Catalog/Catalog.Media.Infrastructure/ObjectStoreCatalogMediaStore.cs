using System.Security.Cryptography;
using Aggregator.CatalogMedia.Application;
using Aggregator.CatalogMedia.Domain;
using Platform.ObjectStorage;

namespace Aggregator.CatalogMedia.Infrastructure;

public sealed class ObjectStoreCatalogMediaStore(IObjectStore objectStore) : ICatalogMediaObjectStore
{
    public async Task<CatalogMediaUploadAuthorization> CreateUploadAuthorizationAsync(
        CatalogMediaAsset asset,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var upload = await objectStore.CreateScopedWriteUrlAsync(
            asset.QuarantineObjectKey,
            asset.ExpectedContentType,
            lifetime,
            cancellationToken);
        return new CatalogMediaUploadAuthorization(
            upload.Url,
            upload.ExpiresAtUtc,
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    public async Task<CatalogMediaObjectDescriptor> VerifyUploadedAsync(
        CatalogMediaAsset asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var descriptor = await objectStore.HeadAsync(asset.QuarantineObjectKey, cancellationToken)
            ?? throw Failure(
                "CATALOG_MEDIA_OBJECT_NOT_FOUND",
                "Registered media object is absent from quarantine storage.",
                "Upload the exact object before completing the command.");
        var key = descriptor.Key;
        var contentType = descriptor.ContentType;
        var digest = descriptor.Sha256;
        var size = Convert.ToInt64(descriptor.Size, System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(key, asset.QuarantineObjectKey, StringComparison.Ordinal) ||
            !string.Equals(contentType, asset.ExpectedContentType, StringComparison.Ordinal) ||
            !string.Equals(digest, asset.ExpectedContentDigest, StringComparison.Ordinal) ||
            size != asset.ExpectedSize)
        {
            throw Failure(
                "CATALOG_MEDIA_OBJECT_METADATA_MISMATCH",
                "Quarantine object metadata differs from the registered media identity.",
                "Delete the divergent object and upload the exact registered bytes.");
        }
        await using var verified = await objectStore.OpenReadVerifiedAsync(
            asset.QuarantineObjectKey,
            asset.ExpectedContentDigest,
            cancellationToken);
        var buffer = new byte[64 * 1024];
        long observed = 0;
        while (true)
        {
            var read = await verified.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            observed += read;
            if (observed > asset.ExpectedSize)
            {
                throw Failure(
                    "CATALOG_MEDIA_OBJECT_SIZE_MISMATCH",
                    "Verified media stream exceeds the registered size.",
                    "Replace the quarantine object with the exact registered bytes.");
            }
        }
        if (observed != asset.ExpectedSize)
        {
            throw Failure(
                "CATALOG_MEDIA_OBJECT_SIZE_MISMATCH",
                "Verified media stream length differs from the registered size.",
                "Replace the quarantine object with the exact registered bytes.");
        }
        return new CatalogMediaObjectDescriptor(key, contentType, digest, size);
    }

    public Task<Stream> OpenQuarantineReadAsync(
        CatalogMediaAsset asset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return objectStore.OpenReadVerifiedAsync(
            asset.QuarantineObjectKey,
            asset.ExpectedContentDigest,
            cancellationToken);
    }

    public async Task<CatalogMediaObjectDescriptor> PutVariantAsync(
        CatalogMediaAsset asset,
        CatalogMediaVariantKind kind,
        string contentType,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (content.IsEmpty)
        {
            throw Failure(
                "CATALOG_MEDIA_VARIANT_EMPTY",
                "Generated media variant is empty.",
                "Correct the image-processing owner before retrying.");
        }
        var extension = contentType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => throw Failure(
                "CATALOG_MEDIA_VARIANT_CONTENT_TYPE_UNSUPPORTED",
                $"Generated media type '{contentType}' is unsupported.",
                "Emit one of the allowlisted image content types."),
        };
        var key = $"catalog-media/published/{asset.CatalogKey}/{asset.Id:N}/{kind.ToString().ToLowerInvariant()}.{extension}";
        var digest = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        await using var stream = new MemoryStream(content.ToArray(), writable: false);
        _ = await objectStore.PutVerifiedAsync(
            key,
            stream,
            content.Length,
            digest,
            contentType,
            cancellationToken);
        return new CatalogMediaObjectDescriptor(key, contentType, digest, content.Length);
    }

    public Task DeleteQuarantineAsync(CatalogMediaAsset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return objectStore.DeleteAsync(asset.QuarantineObjectKey, cancellationToken);
    }

    private static CatalogMediaApplicationException Failure(string code, string message, string action) =>
        new("CatalogMedia.ObjectStorage", code, 422, message, action);
}
