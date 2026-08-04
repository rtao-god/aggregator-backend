namespace Aggregator.Ingestion.Application;

/// <summary>Defines the active package representation accepted by the Ingestion upload and reader path.</summary>
public static class IngestionPayloadUploadPolicy
{
    public const string ContentType = "application/json";

    public const string QuarantinePrefix = "ingestion/quarantine/";

    public static void Validate(string objectKey, string contentType, long size)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            objectKey.Length > 1024 ||
            !objectKey.StartsWith(QuarantinePrefix, StringComparison.Ordinal) ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.Contains('\\'))
        {
            throw new IngestionApplicationException(
                "Ingestion.Payload",
                "INGESTION_PAYLOAD_OBJECT_KEY_INVALID",
                422,
                "The payload object key is outside the Ingestion quarantine namespace.",
                "Register the package with an exact object key under ingestion/quarantine/.");
        }

        if (!string.Equals(contentType, ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new IngestionApplicationException(
                "Ingestion.Payload",
                "INGESTION_PAYLOAD_CONTENT_TYPE_UNSUPPORTED",
                422,
                $"Payload content type '{contentType}' is unsupported by the active package reader.",
                "Serialize the exact package as application/json before registration or upload.");
        }

        if (size <= 0)
        {
            throw new IngestionApplicationException(
                "Ingestion.Payload",
                "INGESTION_PAYLOAD_SIZE_INVALID",
                500,
                "The validated manifest supplied a non-positive payload size.",
                "Correct the manifest validation and persistence owner before requesting an upload URL.");
        }
    }
}
