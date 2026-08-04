using System.Globalization;
using System.Text.Json;

namespace Aggregator.Catalog.Domain;

public enum AttributeDataType
{
    Boolean = 1,
    Integer = 2,
    Decimal = 3,
    Money = 4,
    ShortText = 5,
    LongText = 6,
    LocalizedText = 7,
    Date = 8,
    DateTime = 9,
    Duration = 10,
    SingleOption = 11,
    MultiOption = 12,
    Measurement = 13,
    PhoneCapability = 14,
    ExternalReferenceCapability = 15,
    GeoClassification = 16,
}

public enum AttributeValueState
{
    Observed = 1,
    OwnerConfirmed = 2,
    EditorConfirmed = 3,
    Unknown = 4,
    NotDisclosed = 5,
    NotApplicable = 6,
    Disputed = 7,
    Expired = 8,
}

public sealed class ListingAttributeValue
{
    private readonly JsonElement? _value;

    private ListingAttributeValue(
        string attributeKey,
        AttributeDataType dataType,
        AttributeValueState state,
        JsonElement? value)
    {
        AttributeKey = attributeKey;
        DataType = dataType;
        State = state;
        _value = value?.Clone();
    }

    public string AttributeKey { get; }

    public AttributeDataType DataType { get; }

    public AttributeValueState State { get; }

    public JsonElement? Value => _value?.Clone();

    public static ListingAttributeValue Create(
        string attributeKey,
        AttributeDataType dataType,
        AttributeValueState state,
        JsonElement? value)
    {
        CatalogTextRules.RequireKey(attributeKey, nameof(attributeKey));
        var missingState = state is AttributeValueState.Unknown
            or AttributeValueState.NotDisclosed
            or AttributeValueState.NotApplicable;
        if (missingState)
        {
            if (value is not null && value.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            {
                throw new CatalogDomainException(
                    "ATTRIBUTE_MISSING_STATE_HAS_VALUE",
                    $"Attribute '{attributeKey}' in state '{state}' cannot carry a value.");
            }

            return new ListingAttributeValue(attributeKey, dataType, state, null);
        }

        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new CatalogDomainException(
                "ATTRIBUTE_VALUE_REQUIRED",
                $"Attribute '{attributeKey}' in state '{state}' requires a typed value.");
        }

        ValidateValue(attributeKey, dataType, value.Value);
        return new ListingAttributeValue(attributeKey, dataType, state, value);
    }

    private static void ValidateValue(string attributeKey, AttributeDataType dataType, JsonElement value)
    {
        var valid = dataType switch
        {
            AttributeDataType.Boolean or AttributeDataType.PhoneCapability or AttributeDataType.ExternalReferenceCapability =>
                value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            AttributeDataType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            AttributeDataType.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            AttributeDataType.Money => IsMoney(value),
            AttributeDataType.ShortText => IsText(value, 500),
            AttributeDataType.LongText => IsText(value, 20_000),
            AttributeDataType.LocalizedText => IsLocalizedText(value),
            AttributeDataType.Date => IsDate(value),
            AttributeDataType.DateTime => IsDateTime(value),
            AttributeDataType.Duration => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var duration) && duration >= 0,
            AttributeDataType.SingleOption or AttributeDataType.GeoClassification => IsText(value, 100),
            AttributeDataType.MultiOption => IsStringArray(value),
            AttributeDataType.Measurement => IsMeasurement(value),
            _ => false,
        };

        if (!valid)
        {
            throw new CatalogDomainException(
                "ATTRIBUTE_VALUE_TYPE_MISMATCH",
                $"Attribute '{attributeKey}' does not satisfy data type '{dataType}'.");
        }
    }

    private static bool IsMoney(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("amount", out var amount)
            || !amount.TryGetDecimal(out _)
            || !value.TryGetProperty("currency", out var currency)
            || currency.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var code = currency.GetString();
        return code is { Length: 3 } && code.All(character => char.IsAsciiLetter(character) && char.IsUpper(character));
    }

    private static bool IsMeasurement(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty("value", out var numericValue)
        && numericValue.TryGetDecimal(out _)
        && value.TryGetProperty("unit", out var unit)
        && IsText(unit, 50);

    private static bool IsLocalizedText(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var count = 0;
        foreach (var property in value.EnumerateObject())
        {
            try
            {
                CatalogTextRules.RequireLocale(property.Name, nameof(value));
            }
            catch (CatalogDomainException)
            {
                return false;
            }

            if (!IsText(property.Value, 20_000))
            {
                return false;
            }

            count++;
        }

        return count > 0;
    }

    private static bool IsStringArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (!IsText(item, 100) || !seen.Add(item.GetString()!))
            {
                return false;
            }
        }

        return seen.Count > 0;
    }

    private static bool IsText(JsonElement value, int maximumLength) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is { } text
        && !string.IsNullOrWhiteSpace(text)
        && text.Length <= maximumLength;

    private static bool IsDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateOnly.TryParseExact(
            value.GetString(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    private static bool IsDateTime(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
        && parsed.Offset == TimeSpan.Zero;
}
