using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

internal static class CatalogCanonicalJson
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static byte[] SerializeConfiguration(ProductConfigurationContract configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Serialize(NormalizeConfiguration(configuration));
    }

    public static byte[] SerializeListingContent(ListingRevisionContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Serialize(new
        {
            names = content.Names.Values
                .OrderBy(value => value.Locale.Value, StringComparer.Ordinal)
                .Select(value => new
                {
                    locale = value.Locale.Value,
                    state = value.State,
                    value = value.Value,
                    assertionId = value.AssertionId,
                    missingReason = value.MissingReason,
                })
                .ToArray(),
            descriptions = content.Descriptions.Values
                .OrderBy(value => value.Locale.Value, StringComparer.Ordinal)
                .Select(value => new
                {
                    locale = value.Locale.Value,
                    state = value.State,
                    value = value.Value,
                    assertionId = value.AssertionId,
                    missingReason = value.MissingReason,
                })
                .ToArray(),
            categories = content.Categories
                .OrderBy(value => value.CategoryKey.Value, StringComparer.Ordinal)
                .ThenBy(value => value.AssertionId)
                .Select(value => new
                {
                    categoryKey = value.CategoryKey.Value,
                    value.AssertionId,
                })
                .ToArray(),
            attributes = content.Attributes.Values
                .OrderBy(value => value.Key.Value, StringComparer.Ordinal)
                .Select(value => new
                {
                    attributeKey = value.Key.Value,
                    state = value.State,
                    value = value.Value is null
                        ? null
                        : new
                        {
                            kind = value.Value.Kind,
                            booleanValue = value.Value.BooleanValue,
                            decimalValue = value.Value.DecimalValue,
                            textValue = value.Value.TextValue,
                            textSetValue = value.Value.TextSetValue?.Order(StringComparer.Ordinal).ToArray(),
                        },
                    assertionId = value.AssertionId,
                    missingReason = value.MissingReason,
                })
                .ToArray(),
            geography = new
            {
                state = content.Geography.State,
                latitude = content.Geography.Latitude,
                longitude = content.Geography.Longitude,
                districtKey = content.Geography.DistrictKey,
                assertionId = content.Geography.AssertionId,
            },
            contacts = content.Contacts
                .OrderBy(value => value.Kind)
                .ThenBy(value => value.Target, StringComparer.Ordinal)
                .ThenBy(value => value.AssertionId)
                .Select(value => new
                {
                    contactId = value.Id,
                    kind = value.Kind,
                    target = value.Target,
                    label = value.Label,
                    assertionId = value.AssertionId,
                })
                .ToArray(),
            media = content.Media
                .OrderBy(value => value.DisplayOrder)
                .ThenBy(value => value.MediaId)
                .ThenBy(value => value.VariantId)
                .Select(value => new
                {
                    mediaId = value.MediaId,
                    mediaAggregateRevision = value.MediaAggregateRevision,
                    variantId = value.VariantId,
                    objectUri = value.ObjectUri,
                    contentType = value.ContentType,
                    contentDigest = value.ContentDigest,
                    rightsBasis = value.RightsBasis,
                    displayOrder = value.DisplayOrder,
                    caption = value.Caption,
                    assertionId = value.AssertionId,
                })
                .ToArray(),
            assertions = content.Assertions.Values
                .OrderBy(value => value.Id)
                .Select(value => new
                {
                    value.Id,
                    value.SourceKind,
                    value.SourceReference,
                    value.ObservedAtUtc,
                    value.RecordedAtUtc,
                    value.UsagePolicy,
                    value.EvidenceDigest,
                })
                .ToArray(),
        });
    }

    public static byte[] SerializePublication(CatalogPublicationArtifact publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return Serialize(publication);
    }

    public static byte[] SerializePublicationRequest(CreateCatalogPublicationRequest request)
    {
        CatalogPublicationRequestValidator.Validate(request);
        return Serialize(new CreateCatalogPublicationRequest(
            request.CatalogKey.Trim(),
            request.ConfigurationRevisionId,
            request.ExpectedCurrent,
            request.Selections
                .OrderBy(selection => selection.ListingId)
                .ThenBy(selection => selection.ListingRevisionId)
                .ToArray()));
    }

    public static CreateCatalogPublicationRequest DeserializePublicationRequest(ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty)
        {
            throw new CatalogContractException(
                "catalog.publication_operation_request_empty",
                "Persisted publication request document cannot be empty.");
        }

        try
        {
            var request = JsonSerializer.Deserialize<CreateCatalogPublicationRequest>(content, SerializerOptions)
                ?? throw new CatalogContractException(
                    "catalog.publication_operation_request_invalid",
                    "Persisted publication request document deserialized to an empty contract.");
            CatalogPublicationRequestValidator.Validate(request);
            return request;
        }
        catch (JsonException exception)
        {
            throw new CatalogContractException(
                "catalog.publication_operation_request_invalid",
                $"Persisted publication request document is invalid: {exception.Message}");
        }
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

    private static CatalogContractException NullContractItem(string itemKind) =>
        new(
            "catalog.contract_collection_item_null",
            $"Catalog contract collection contains a null {itemKind}.");

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(
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
                throw new InvalidOperationException($"Unsupported JSON token '{element.ValueKind}'.");
        }
    }
}
