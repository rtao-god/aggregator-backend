using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;

namespace Aggregator.Catalog.Application;

internal static class CatalogCanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] SerializeConfiguration(ProductConfigurationContract configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Serialize(NormalizeConfiguration(configuration));
    }

    public static byte[] SerializeListingContent(ListingRevisionContentContract content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Serialize(NormalizeListingContent(content));
    }

    public static byte[] SerializePublication(CatalogPublicationArtifactV1 publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return Serialize(publication);
    }

    public static string SerializeEvent<T>(T integrationEvent)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        return Encoding.UTF8.GetString(Serialize(integrationEvent));
    }

    public static string ComputeSha256(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static byte[] Serialize<T>(T value)
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

    private static ProductConfigurationContract NormalizeConfiguration(ProductConfigurationContract configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration.Site);
        ArgumentNullException.ThrowIfNull(configuration.Catalog);
        ArgumentNullException.ThrowIfNull(configuration.Categories);
        ArgumentNullException.ThrowIfNull(configuration.Attributes);
        ArgumentNullException.ThrowIfNull(configuration.Site.SupportedLocales);
        ArgumentNullException.ThrowIfNull(configuration.Catalog.AllowedListingKinds);

        var site = new SiteDefinitionContract(
            configuration.Site.Key,
            configuration.Site.DefaultLocale,
            configuration.Site.SupportedLocales.Order(StringComparer.Ordinal).ToArray(),
            configuration.Site.Currency,
            configuration.Site.TimeZone);
        var catalog = new CatalogDefinitionContract(
            configuration.Catalog.Key,
            configuration.Catalog.SiteKey,
            configuration.Catalog.MarketAreaKey,
            configuration.Catalog.Currency,
            configuration.Catalog.TimeZone,
            configuration.Catalog.AllowedListingKinds.OrderBy(kind => (int)kind).ToArray());
        var categories = configuration.Categories
            .Select(category => category ?? throw new CatalogContractException(
                "catalog.configuration_category_null",
                "Product configuration cannot contain a null category definition."))
            .OrderBy(category => category.Key, StringComparer.Ordinal)
            .Select(category =>
            {
                ArgumentNullException.ThrowIfNull(category.SubjectKinds);
                ArgumentNullException.ThrowIfNull(category.LocalizedNames);
                return new CategoryDefinitionContract(
                    category.Key,
                    category.SubjectKinds.OrderBy(kind => (int)kind).ToArray(),
                    category.LocalizedNames,
                    category.IsActive);
            })
            .ToArray();
        var attributes = configuration.Attributes
            .Select(attribute => attribute ?? throw new CatalogContractException(
                "catalog.configuration_attribute_null",
                "Product configuration cannot contain a null attribute definition."))
            .OrderBy(attribute => attribute.Key, StringComparer.Ordinal)
            .Select(attribute =>
            {
                ArgumentNullException.ThrowIfNull(attribute.Categories);
                ArgumentNullException.ThrowIfNull(attribute.LocalizedNames);
                ArgumentNullException.ThrowIfNull(attribute.AllowedValues);
                return new AttributeDefinitionContract(
                    attribute.Key,
                    attribute.ValueKind,
                    attribute.Cardinality,
                    attribute.Requirement,
                    attribute.Categories.Order(StringComparer.Ordinal).ToArray(),
                    attribute.LocalizedNames,
                    attribute.Minimum,
                    attribute.Maximum,
                    attribute.AllowedValues.Order(StringComparer.Ordinal).ToArray(),
                    attribute.IsFilterable,
                    attribute.IsSortable);
            })
            .ToArray();

        return new ProductConfigurationContract(
            configuration.RevisionId,
            configuration.CreatedAtUtc,
            site,
            catalog,
            categories,
            attributes);
    }

    private static ListingRevisionContentContract NormalizeListingContent(ListingRevisionContentContract content)
    {
        ArgumentNullException.ThrowIfNull(content.Names);
        ArgumentNullException.ThrowIfNull(content.Descriptions);
        ArgumentNullException.ThrowIfNull(content.Categories);
        ArgumentNullException.ThrowIfNull(content.Attributes);
        ArgumentNullException.ThrowIfNull(content.Geography);
        ArgumentNullException.ThrowIfNull(content.Contacts);
        ArgumentNullException.ThrowIfNull(content.Media);
        ArgumentNullException.ThrowIfNull(content.Assertions);

        return new ListingRevisionContentContract(
            content.Names
                .Select(value => value ?? throw NullContractItem("localized name"))
                .OrderBy(value => value.Locale, StringComparer.Ordinal)
                .ToArray(),
            content.Descriptions
                .Select(value => value ?? throw NullContractItem("localized description"))
                .OrderBy(value => value.Locale, StringComparer.Ordinal)
                .ToArray(),
            content.Categories
                .Select(value => value ?? throw NullContractItem("category assignment"))
                .OrderBy(value => value.CategoryKey, StringComparer.Ordinal)
                .ThenBy(value => value.AssertionId)
                .ToArray(),
            content.Attributes
                .Select(value => value ?? throw NullContractItem("attribute value"))
                .OrderBy(value => value.AttributeKey, StringComparer.Ordinal)
                .Select(value => value with
                {
                    Value = value.Value is null
                        ? null
                        : value.Value with
                        {
                            TextSetValue = value.Value.TextSetValue?.Order(StringComparer.Ordinal).ToArray(),
                        },
                })
                .ToArray(),
            content.Geography,
            content.Contacts
                .Select(value => value ?? throw NullContractItem("contact"))
                .OrderBy(value => (int)value.Kind)
                .ThenBy(value => value.Target, StringComparer.Ordinal)
                .ToArray(),
            content.Media
                .Select(value => value ?? throw NullContractItem("media reference"))
                .OrderBy(value => value.MediaId)
                .ToArray(),
            content.Assertions
                .Select(value => value ?? throw NullContractItem("provenance assertion"))
                .OrderBy(value => value.Id)
                .ToArray());
    }

    private static CatalogContractException NullContractItem(string itemKind) =>
        new(
            "catalog.contract_collection_item_null",
            $"Catalog contract collection contains a null {itemKind}."
        );

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
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
