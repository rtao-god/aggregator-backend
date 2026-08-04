namespace Platform.ObjectStorage;

public sealed record StoredObjectDescriptor(
    string Key,
    long Size,
    string Sha256,
    string ContentType,
    string ETag,
    DateTimeOffset LastModifiedUtc);

public sealed record ScopedObjectUrl(Uri Url, DateTimeOffset ExpiresAtUtc);

/// <summary>Technical object-storage boundary; business prefixes and retention remain context-owned.</summary>
public interface IObjectStore
{
    public Task<StoredObjectDescriptor> PutVerifiedAsync(
        string key,
        Stream content,
        long expectedSize,
        string expectedSha256,
        string contentType,
        CancellationToken cancellationToken);

    public Task<StoredObjectDescriptor> HeadAsync(string key, CancellationToken cancellationToken);

    public Task<Stream> OpenReadVerifiedAsync(
        string key,
        string expectedSha256,
        CancellationToken cancellationToken);

    public Task<ScopedObjectUrl> CreateScopedReadUrlAsync(
        string key,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    public Task<ScopedObjectUrl> CreateScopedWriteUrlAsync(
        string key,
        string contentType,
        TimeSpan lifetime,
        CancellationToken cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken);
}
