using System.Buffers;
using System.Security.Cryptography;
using Platform.ObjectStorage;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Reads one exact verified Ingestion package object without selecting a latest or adjacent artifact.</summary>
public sealed class IngestionPackageObjectReader(IObjectStore objectStore)
{
    private const string QuarantinePrefix = "ingestion/quarantine/";

    public async Task<byte[]> ReadVerifiedAsync(
        string objectKey,
        string expectedDigest,
        long expectedSize,
        long maximumSize,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(objectKey, expectedDigest, expectedSize, maximumSize);
        var metadata = await objectStore.HeadAsync(objectKey, cancellationToken)
            ?? throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_OBJECT_MISSING",
                $"Ingestion package object '{objectKey}' does not exist.");
        if (metadata.Size != expectedSize)
        {
            throw new IngestionPackageIntegrityException(
                "INGESTION_PAYLOAD_SIZE_MISMATCH",
                $"Expected package size {expectedSize}, actual size {metadata.Size}.");
        }

        await using var stream = await objectStore.OpenReadAsync(objectKey, cancellationToken);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var buffer = new MemoryStream(
            expectedSize <= int.MaxValue
                ? checked((int)expectedSize)
                : 0);
        var rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(rented.AsMemory(), cancellationToken)) > 0)
            {
                total += read;
                if (total > maximumSize || total > expectedSize)
                {
                    throw new IngestionPackageIntegrityException(
                        "INGESTION_PAYLOAD_SIZE_MISMATCH",
                        "The package object exceeded its registered or configured size while reading.");
                }

                digest.AppendData(rented, 0, read);
                await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
            }

            var actualDigest = Convert.ToHexStringLower(digest.GetHashAndReset());
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
            {
                throw new IngestionPackageIntegrityException(
                    "INGESTION_PAYLOAD_DIGEST_MISMATCH",
                    $"Expected package digest '{expectedDigest}', actual '{actualDigest}'.");
            }

            if (total != expectedSize)
            {
                throw new IngestionPackageIntegrityException(
                    "INGESTION_PAYLOAD_SIZE_MISMATCH",
                    $"Expected package size {expectedSize}, actual size {total}.");
            }

            return buffer.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
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
            objectKey.EndsWith("/", StringComparison.Ordinal))
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

public sealed class IngestionPackageIntegrityException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
