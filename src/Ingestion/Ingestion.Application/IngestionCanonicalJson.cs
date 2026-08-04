using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aggregator.Ingestion.Application;

public static class IngestionCanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] Serialize<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var element = JsonSerializer.SerializeToElement(value, SerializerOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(element, writer);
        }

        return buffer.ToArray();
    }

    public static T Deserialize<T>(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Serialization",
                "INGESTION_DOCUMENT_EMPTY",
                500,
                "A persisted canonical Ingestion document is empty.",
                "Restore the exact owner document from a verified database backup.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value, SerializerOptions)
                ?? throw new IngestionApplicationException(
                    "Ingestion.Serialization",
                    "INGESTION_DOCUMENT_NULL",
                    500,
                    "A persisted canonical Ingestion document deserialized to null.",
                    "Restore the exact owner document from a verified database backup.");
        }
        catch (JsonException exception)
        {
            throw new IngestionApplicationException(
                "Ingestion.Serialization",
                "INGESTION_DOCUMENT_INVALID",
                500,
                "A persisted canonical Ingestion document is invalid for its owner contract.",
                "Restore the exact owner document from a verified database backup.",
                innerException: exception);
        }
    }

    public static string ComputeDigest<T>(T value) =>
        Convert.ToHexString(SHA256.HashData(Serialize(value))).ToLowerInvariant();

    public static string ComputeDigest(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return ComputeDigest(value.AsSpan());
    }

    public static string ComputeDigest(ReadOnlySpan<byte> value)
    {
        if (value.IsEmpty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Serialization",
                "INGESTION_DOCUMENT_EMPTY",
                500,
                "A canonical Ingestion document cannot be empty when computing its digest.",
                "Correct the canonical document producer before persistence or verification.");
        }

        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name.Normalize(NormalizationForm.FormC));
                    WriteCanonical(property.Value, writer);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString()!.Normalize(NormalizationForm.FormC));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: false);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException(
                    $"JSON value kind '{element.ValueKind}' cannot be canonicalized.");
        }
    }
}
