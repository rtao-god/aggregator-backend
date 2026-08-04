using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Aggregator.Catalog.Contracts;

public sealed record CatalogIngestionCommandDigestInput(
    Guid CommandId,
    Guid IngestionBatchId,
    string IngestionItemKey,
    string SiteKey,
    string CatalogKey,
    Guid ExpectedCatalogConfigurationRevisionId,
    string EntityKind,
    string SubjectNaturalKey,
    IReadOnlyList<CatalogDraftFieldValueContract> Fields,
    DateTimeOffset RequestedAtUtc);

public static class CatalogIngestionCommandDigest
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string Compute(CatalogIngestionUpsertDraftCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Compute(new CatalogIngestionCommandDigestInput(
            command.CommandId,
            command.IngestionBatchId,
            command.IngestionItemKey,
            command.SiteKey,
            command.CatalogKey,
            command.ExpectedCatalogConfigurationRevisionId,
            command.EntityKind,
            command.SubjectNaturalKey,
            command.Fields,
            command.RequestedAtUtc));
    }

    public static string Compute(CatalogIngestionCommandDigestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var element = JsonSerializer.SerializeToElement(input, SerializerOptions);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            WriteCanonical(element, writer);
        }

        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
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
