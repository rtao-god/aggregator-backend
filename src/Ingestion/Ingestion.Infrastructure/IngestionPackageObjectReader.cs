using Aggregator.Ingestion.Application;
using Platform.ObjectStorage;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Reads only the exact registered quarantine object after metadata and stream digest proof.</summary>
public sealed class IngestionPackageObjectReader(IObjectStore objectStore) : IIngestionPackageObjectReader
{
    private const string QuarantinePrefix = "ingestion/quarantine/";
    private readonly IObjectStore _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));

    public async Task<byte[]> ReadExactAsync(
        string objectKey,
        string expectedDigest,
        long expectedSize,
        long maximumSize,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(objectKey, expectedDigest, expectedSize, maximumSize);
        var descriptor = await _objectStore.HeadAsync(objectKey, cancellationToken);
        if (!string.Equals(descriptor.Key, objectKey, StringComparison.Ordinal) ||
            descriptor.Size != expectedSize ||
            !string.Equals(descriptor.Sha256, expectedDigest, StringComparison.Ordinal) ||
            !string.Equals(descriptor.ContentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_OBJECT_IDENTITY_MISMATCH",
                "The stored package metadata does not match the exact registered object identity.");
        }

        await using var stream = await _objectStore.OpenReadVerifiedAsync(
            objectKey,
            expectedDigest,
            cancellationToken);
        using var buffer = expectedSize <= int.MaxValue
            ? new MemoryStream((int)expectedSize)
            : new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumSize || total > expectedSize)
            {
                throw new IngestionPackageIntegrityException(
                    "INGESTION_PAYLOAD_SIZE_EXCEEDED",
                    "The stored package exceeds its registered or configured maximum size.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        if (total != expectedSize)
        {
            throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_SIZE_MISMATCH",
                $"Expected package size {expectedSize}, actual size {total}.");
        }

        return buffer.ToArray();
    }

    private static void ValidateIdentity(
        string objectKey,
        string expectedDigest,
        long expectedSize,
        long maximumSize)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            !objectKey.StartsWith(QuarantinePrefix, StringComparison.Ordinal) ||
            objectKey.Length > 1024 ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.Contains('\\') ||
            objectKey.EndsWith('/', StringComparison.Ordinal))
        {
            throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_OBJECT_KEY_INVALID",
                "The package object key is outside the Ingestion quarantine namespace or is traversal-prone.");
        }

        if (expectedDigest is not { Length: 64 } ||
            expectedDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_OBJECT_DIGEST_INVALID",
                "The registered package object digest is invalid.");
        }

        if (expectedSize <= 0 || maximumSize <= 0 || expectedSize > maximumSize)
        {
            throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_OBJECT_SIZE_INVALID",
                "The registered package object size is invalid or exceeds the configured limit.");
        }
    }
}
