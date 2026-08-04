using Aggregator.Ingestion.Application;
using Platform.ObjectStorage;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Adapts the technical object store to the Ingestion quarantine payload contract.</summary>
public sealed class ObjectStoreIngestionPayloadStore : IIngestionPayloadStore
{
    private const string QuarantinePrefix = "ingestion/quarantine/";

    private readonly IObjectStore _objectStore;
    private readonly IIngestionClock _clock;

    public ObjectStoreIngestionPayloadStore(IObjectStore objectStore, IIngestionClock clock)
    {
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IngestionUploadAuthorization> CreateUploadAuthorizationAsync(
        string objectKey,
        string contentType,
        long maximumSize,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);
        if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_PAYLOAD_CONTENT_TYPE_UNSUPPORTED",
                422,
                $"Payload content type '{contentType}' is unsupported by the active package reader.",
                "Serialize the exact package as application/json before requesting an upload URL.");
        }

        if (maximumSize is < 1 or > IngestionPackageValidator.MaximumPayloadBytes)
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_PAYLOAD_SIZE_INVALID",
                422,
                "The requested payload size is outside the supported Ingestion package limit.",
                "Split the collector export into explicit bounded packages.");
        }

        if (lifetime < TimeSpan.FromMinutes(1) || lifetime > TimeSpan.FromMinutes(15))
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_UPLOAD_LIFETIME_INVALID",
                500,
                "Upload authorization lifetime must be between one and fifteen minutes.",
                "Correct the Ingestion upload policy configuration.");
        }

        var issuedAtUtc = _clock.GetUtcNow();
        if (issuedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new IngestionApplicationException(
                "Ingestion.Clock",
                "INGESTION_CLOCK_NOT_UTC",
                500,
                "The Ingestion clock returned a non-UTC timestamp.",
                "Correct the composition root to supply a UTC clock.");
        }

        var uploadUri = await _objectStore.CreateScopedWriteAsync(
            objectKey,
            contentType,
            lifetime,
            cancellationToken);
        return new IngestionUploadAuthorization(
            uploadUri,
            objectKey,
            issuedAtUtc.Add(lifetime),
            contentType,
            maximumSize);
    }

    public async Task<IngestionPayloadDescriptor> VerifyUploadedAsync(
        string objectKey,
        string expectedContentDigest,
        long expectedSize,
        string expectedContentType,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);
        ValidateDigest(expectedContentDigest);
        if (expectedSize <= 0)
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_PAYLOAD_SIZE_INVALID",
                500,
                "The registered payload size must be positive.",
                "Correct the registered manifest before upload completion.");
        }

        try
        {
            var descriptor = await _objectStore.HeadAsync(objectKey, cancellationToken)
                ?? throw new IngestionApplicationException(
                    "Ingestion.ObjectStorage",
                    "INGESTION_PAYLOAD_OBJECT_MISSING",
                    409,
                    $"Payload object '{objectKey}' does not exist.",
                    "Upload the exact registered object before completing the upload.");
            if (!string.Equals(descriptor.Key, objectKey, StringComparison.Ordinal) ||
                descriptor.Size != expectedSize ||
                !string.Equals(
                    descriptor.ContentType,
                    expectedContentType,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IngestionApplicationException(
                    "Ingestion.ObjectStorage",
                    "INGESTION_PAYLOAD_OBJECT_METADATA_MISMATCH",
                    422,
                    "The uploaded object metadata does not match the registered manifest.",
                    "Delete or replace the quarantined object through the owner upload flow and retry.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["objectKey"] = objectKey,
                        ["expectedSize"] = expectedSize,
                        ["actualSize"] = descriptor.Size,
                        ["expectedContentType"] = expectedContentType,
                        ["actualContentType"] = descriptor.ContentType,
                    });
            }

            await using var verified = await _objectStore.OpenReadVerifiedAsync(
                objectKey,
                expectedContentDigest,
                cancellationToken);
            return new IngestionPayloadDescriptor(
                descriptor.Key,
                expectedContentDigest,
                descriptor.Size,
                descriptor.ContentType,
                descriptor.LastModifiedAtUtc);
        }
        catch (IngestionApplicationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_PAYLOAD_VERIFICATION_FAILED",
                503,
                "The uploaded payload could not be verified through the object-store owner contract.",
                "Inspect the object-store diagnostic and retry only after the dependency is healthy.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["objectKey"] = objectKey,
                    ["expectedContentDigest"] = expectedContentDigest,
                    ["expectedSize"] = expectedSize,
                },
                exception);
        }
    }

    private static void ValidateObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            objectKey.Length > 1024 ||
            !objectKey.StartsWith(QuarantinePrefix, StringComparison.Ordinal) ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.Contains('\\'))
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_PAYLOAD_OBJECT_KEY_INVALID",
                422,
                "The payload object key is outside the Ingestion quarantine namespace.",
                "Use the exact owner-generated key under ingestion/quarantine/.");
        }
    }

    private static void ValidateDigest(string digest)
    {
        if (digest is not { Length: 64 } ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new IngestionApplicationException(
                "Ingestion.ObjectStorage",
                "INGESTION_PAYLOAD_DIGEST_INVALID",
                500,
                "The registered payload digest is not a lowercase SHA-256 value.",
                "Correct the manifest validation path before upload completion.");
        }
    }
}
