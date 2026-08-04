using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace Platform.ObjectStorage;

public sealed record S3ObjectStoreOptions
{
    public required Uri ServiceUrl { get; init; }

    public required string Region { get; init; }

    public required string Bucket { get; init; }

    public required string AccessKey { get; init; }

    public required string SecretKey { get; init; }

    public bool ForcePathStyle { get; init; } = true;

    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(ServiceUrl);
        if (ServiceUrl.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Object-store endpoint must use HTTP or HTTPS.", nameof(ServiceUrl));
        }

        RequireText(Region, nameof(Region));
        RequireText(Bucket, nameof(Bucket));
        RequireText(AccessKey, nameof(AccessKey));
        RequireText(SecretKey, nameof(SecretKey));
    }

    private static void RequireText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", name);
        }
    }
}

/// <summary>S3-compatible adapter that verifies exact size and SHA-256 after writes and reads.</summary>
public sealed class S3ObjectStore : IObjectStore, IDisposable
{
    private readonly S3ObjectStoreOptions _options;
    private readonly AmazonS3Client _client;

    public S3ObjectStore(S3ObjectStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        var config = new AmazonS3Config
        {
            ServiceURL = _options.ServiceUrl.ToString().TrimEnd('/'),
            AuthenticationRegion = _options.Region,
            ForcePathStyle = _options.ForcePathStyle,
            UseHttp = _options.ServiceUrl.Scheme == Uri.UriSchemeHttp,
        };
        _client = new AmazonS3Client(
            new BasicAWSCredentials(_options.AccessKey, _options.SecretKey),
            config);
    }

    public async Task<StoredObjectDescriptor> PutVerifiedAsync(
        string key,
        Stream content,
        long expectedSize,
        string expectedSha256,
        string contentType,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ValidateDigest(expectedSha256);
        if (expectedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedSize));
        }

        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("Content type is required.", nameof(contentType));
        }

        if (!content.CanRead)
        {
            throw new ArgumentException("Content stream must be readable.", nameof(content));
        }

        if (content.CanSeek && content.Length - content.Position != expectedSize)
        {
            throw new InvalidOperationException("Content length does not match the declared expected size.");
        }

        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = contentType,
            DisablePayloadSigning = false,
        };
        request.Metadata["sha256"] = expectedSha256;
        await _client.PutObjectAsync(request, cancellationToken);

        var descriptor = await HeadAsync(key, cancellationToken);
        if (descriptor.Size != expectedSize || !string.Equals(descriptor.Sha256, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Stored object '{key}' failed post-upload integrity verification. Expected {expectedSize}/{expectedSha256}, got {descriptor.Size}/{descriptor.Sha256}.");
        }

        return descriptor;
    }

    public async Task<StoredObjectDescriptor> HeadAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        var response = await _client.GetObjectMetadataAsync(
            new GetObjectMetadataRequest { BucketName = _options.Bucket, Key = key },
            cancellationToken);
        var digest = response.Metadata["x-amz-meta-sha256"] ?? response.Metadata["sha256"];
        if (string.IsNullOrWhiteSpace(digest))
        {
            throw new InvalidDataException($"Stored object '{key}' has no owner SHA-256 metadata.");
        }

        ValidateDigest(digest);
        return new StoredObjectDescriptor(
            key,
            response.ContentLength,
            digest,
            response.Headers.ContentType ?? "application/octet-stream",
            response.ETag?.Trim('"') ?? string.Empty,
            response.LastModified.ToUniversalTime());
    }

    public async Task<Stream> OpenReadVerifiedAsync(
        string key,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        ValidateKey(key);
        ArgumentNullException.ThrowIfNull(expectedSha256);
        ValidateDigest(expectedSha256);
        using var response = await _client.GetObjectAsync(
            new GetObjectRequest { BucketName = _options.Bucket, Key = key },
            cancellationToken);
        var buffer = response.ContentLength > int.MaxValue
            ? new MemoryStream()
            : new MemoryStream((int)response.ContentLength);
        await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;
        var actualDigest = Convert.ToHexString(await SHA256.HashDataAsync(buffer, cancellationToken)).ToLowerInvariant();
        if (!string.Equals(actualDigest, expectedSha256, StringComparison.Ordinal))
        {
            await buffer.DisposeAsync();
            throw new InvalidDataException(
                $"Stored object '{key}' digest mismatch. Expected '{expectedSha256}', actual '{actualDigest}'.");
        }

        buffer.Position = 0;
        return buffer;
    }

    public Task<ScopedObjectUrl> CreateScopedReadUrlAsync(
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        var expiresAt = ValidateLifetime(lifetime);
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = expiresAt.UtcDateTime,
        });
        return Task.FromResult(new ScopedObjectUrl(new Uri(url, UriKind.Absolute), expiresAt));
    }

    public Task<ScopedObjectUrl> CreateScopedWriteUrlAsync(
        string key,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKey(key);
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("Content type is required.", nameof(contentType));
        }

        var expiresAt = ValidateLifetime(lifetime);
        var url = _client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = expiresAt.UtcDateTime,
        });
        return Task.FromResult(new ScopedObjectUrl(new Uri(url, UriKind.Absolute), expiresAt));
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        ValidateKey(key);
        await _client.DeleteObjectAsync(
            new DeleteObjectRequest { BucketName = _options.Bucket, Key = key },
            cancellationToken);
    }

    public void Dispose() => _client.Dispose();

    private static DateTimeOffset ValidateLifetime(TimeSpan lifetime)
    {
        if (lifetime < TimeSpan.FromSeconds(10) || lifetime > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Scoped URL lifetime must be between 10 seconds and 15 minutes.");
        }

        return DateTimeOffset.UtcNow + lifetime;
    }

    private static void ValidateDigest(string digest)
    {
        ArgumentNullException.ThrowIfNull(digest);
        if (digest.Length != 64 || digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 hex digest is required.", nameof(digest));
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("/", StringComparison.Ordinal) || key.Contains("..", StringComparison.Ordinal) || key.Contains('\\'))
        {
            throw new ArgumentException("Object key must be relative and traversal-free.", nameof(key));
        }
    }
}
