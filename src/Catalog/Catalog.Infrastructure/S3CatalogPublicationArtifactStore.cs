using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Aggregator.Catalog.Infrastructure;

public sealed class CatalogObjectStorageOptions
{
    public const string SectionName = "Catalog:ObjectStorage";

    public required string ServiceUrl { get; init; }

    public required string BucketName { get; init; }

    public required string AccessKey { get; init; }

    public required string SecretKey { get; init; }

    public bool ForcePathStyle { get; init; } = true;

    public long MaximumPublicationBytes { get; init; } = 64 * 1024 * 1024;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ServiceUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(BucketName);
        ArgumentException.ThrowIfNullOrWhiteSpace(AccessKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(SecretKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPublicationBytes);
        if (!Uri.TryCreate(ServiceUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException($"Object storage service URL '{ServiceUrl}' is invalid.");
        }
    }
}

public sealed class S3CatalogPublicationArtifactStore(
    IAmazonS3 client,
    IOptions<CatalogObjectStorageOptions> options) : ICatalogPublicationArtifactStore
{
    private readonly CatalogObjectStorageOptions _options = ValidateOptions(options.Value);

    public async Task PutVerifiedAsync(
        string objectKey,
        ReadOnlyMemory<byte> content,
        string sha256Digest,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
        if (objectKey.StartsWith('/') ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.Contains('\\'))
        {
            throw new ArgumentException("Object key must be a normalized relative key.", nameof(objectKey));
        }

        if (content.Length == 0 || content.Length > _options.MaximumPublicationBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(content),
                content.Length,
                $"Publication artifact must contain between 1 and {_options.MaximumPublicationBytes} bytes.");
        }

        var expectedDigestBytes = RequireDigest(sha256Digest);
        var computedDigestBytes = SHA256.HashData(content.Span);
        if (!CryptographicOperations.FixedTimeEquals(expectedDigestBytes, computedDigestBytes))
        {
            throw new InvalidOperationException(
                $"Publication artifact '{objectKey}' does not match declared digest '{sha256Digest}'.");
        }

        var existing = await TryGetMetadataAsync(objectKey, cancellationToken);
        if (existing is not null)
        {
            EnsureMetadata(existing, objectKey, content.Length, sha256Digest);
            await EnsureStoredContentAsync(
                objectKey,
                content.Length,
                expectedDigestBytes,
                cancellationToken);
            return;
        }

        await using var stream = new MemoryStream(content.ToArray(), writable: false);
        var request = new PutObjectRequest
        {
            BucketName = _options.BucketName,
            Key = objectKey,
            InputStream = stream,
            ContentType = "application/json",
            AutoCloseStream = false,
            ChecksumSHA256 = Convert.ToBase64String(expectedDigestBytes),
        };
        request.Metadata["sha256"] = sha256Digest;
        request.Metadata["contract"] = CatalogPublicationArtifactContract.Identity;
        request.Metadata["contract-revision"] = CatalogPublicationArtifactContract.Revision.ToString(
            CultureInfo.InvariantCulture);
        _ = await client.PutObjectAsync(request, cancellationToken);

        var stored = await client.GetObjectMetadataAsync(
            new GetObjectMetadataRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
            },
            cancellationToken);
        EnsureMetadata(stored, objectKey, content.Length, sha256Digest);
        await EnsureStoredContentAsync(
            objectKey,
            content.Length,
            expectedDigestBytes,
            cancellationToken);
    }

    private async Task<GetObjectMetadataResponse?> TryGetMetadataAsync(
        string objectKey,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = _options.BucketName,
                    Key = objectKey,
                },
                cancellationToken);
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task EnsureStoredContentAsync(
        string objectKey,
        long expectedLength,
        byte[] expectedDigest,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetObjectAsync(
            new GetObjectRequest
            {
                BucketName = _options.BucketName,
                Key = objectKey,
            },
            cancellationToken);
        if (response.ContentLength != expectedLength)
        {
            throw new InvalidOperationException(
                $"Read-back publication artifact '{objectKey}' length is '{response.ContentLength}', expected '{expectedLength}'.");
        }

        var storedDigest = await SHA256.HashDataAsync(response.ResponseStream, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(storedDigest, expectedDigest))
        {
            throw new InvalidOperationException(
                $"Read-back publication artifact '{objectKey}' has a mismatched digest.");
        }
    }

    internal static void EnsureMetadata(
        GetObjectMetadataResponse metadata,
        string objectKey,
        long expectedLength,
        string expectedDigest)
    {
        if (metadata.ContentLength != expectedLength)
        {
            throw new InvalidOperationException(
                $"Stored publication artifact '{objectKey}' length is '{metadata.ContentLength}', expected '{expectedLength}'.");
        }

        var storedDigest = metadata.Metadata["x-amz-meta-sha256"];
        if (string.IsNullOrWhiteSpace(storedDigest) ||
            !CryptographicOperations.FixedTimeEquals(
                RequireDigest(storedDigest),
                RequireDigest(expectedDigest)))
        {
            throw new InvalidOperationException(
                $"Stored publication artifact '{objectKey}' has a mismatched digest.");
        }

        var storedContract = metadata.Metadata["x-amz-meta-contract"];
        if (!string.Equals(
                storedContract,
                CatalogPublicationArtifactContract.Identity,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stored publication artifact '{objectKey}' has contract identity '{storedContract}', expected '{CatalogPublicationArtifactContract.Identity}'.");
        }

        var expectedContractRevision = CatalogPublicationArtifactContract.Revision.ToString(
            CultureInfo.InvariantCulture);
        var storedContractRevision = metadata.Metadata["x-amz-meta-contract-revision"];
        if (!string.Equals(
                storedContractRevision,
                expectedContractRevision,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Stored publication artifact '{objectKey}' has contract revision '{storedContractRevision}', expected '{expectedContractRevision}'.");
        }
    }

    private static byte[] RequireDigest(string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        var normalized = digest.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Digest must be a SHA-256 hexadecimal value.", nameof(digest));
        }

        return Convert.FromHexString(normalized);
    }

    private static CatalogObjectStorageOptions ValidateOptions(CatalogObjectStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return options;
    }
}
