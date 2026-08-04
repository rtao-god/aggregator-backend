using System.Text.Json;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

internal static class ListingConfigurationValidator
{
    public static void ValidateForDraft(
        Listing listing,
        ListingRevision revision,
        ProductConfigurationRevisionEnvelope configuration)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateExactOwnerIdentities(listing, revision, configuration);
        var configuredKind = listing.Subject.Kind switch
        {
            ListingKind.Place => ConfiguredListingKind.Place,
            ListingKind.Provider => ConfiguredListingKind.Provider,
            _ => throw InvalidConfiguration("LISTING_KIND_UNSUPPORTED", "The listing kind is not supported by Catalog."),
        };
        if (!configuration.Revision.Definition.Catalog.SupportedListingKinds.Contains(configuredKind))
        {
            throw InvalidConfiguration(
                "LISTING_KIND_NOT_CONFIGURED",
                $"Listing kind '{listing.Subject.Kind}' is not enabled by the exact product configuration revision.");
        }

        var categories = configuration.Revision.Definition.Categories.ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var categoryKey in revision.CategoryKeys)
        {
            if (!categories.TryGetValue(categoryKey, out var category))
            {
                throw InvalidConfiguration("LISTING_CATEGORY_UNKNOWN", $"Category '{categoryKey}' is not defined by the exact taxonomy revision.");
            }

            if (!category.AllowedListingKinds.Contains(configuredKind))
            {
                throw InvalidConfiguration(
                    "LISTING_CATEGORY_KIND_INVALID",
                    $"Category '{categoryKey}' does not allow listing kind '{listing.Subject.Kind}'.");
            }
        }

        var attributeDefinitions = configuration.Revision.Definition.Attributes.ToDictionary(item => item.Key, StringComparer.Ordinal);
        foreach (var value in revision.Attributes)
        {
            if (!attributeDefinitions.TryGetValue(value.AttributeKey, out var definition))
            {
                throw InvalidConfiguration("LISTING_ATTRIBUTE_UNKNOWN", $"Attribute '{value.AttributeKey}' is not defined by the exact attribute revision.");
            }

            if (ToListingType(definition.DataType) != value.DataType)
            {
                throw InvalidConfiguration(
                    "LISTING_ATTRIBUTE_TYPE_MISMATCH",
                    $"Attribute '{value.AttributeKey}' uses '{value.DataType}' but the exact definition requires '{definition.DataType}'.");
            }

            ValidateOptions(value, definition.AllowedOptions);
        }

        ValidateRequiredAttributes(
            revision,
            configuration,
            configuredKind,
            relation => relation.RequiredForDraft,
            "LISTING_DRAFT_ATTRIBUTE_REQUIRED");
    }

    public static void ValidateForPublication(
        Listing listing,
        ListingRevision revision,
        ProductConfigurationRevisionEnvelope configuration,
        DateTimeOffset evaluatedAtUtc)
    {
        ValidateForDraft(listing, revision, configuration);
        var configuredKind = listing.Subject.Kind == ListingKind.Place
            ? ConfiguredListingKind.Place
            : ConfiguredListingKind.Provider;
        ValidateRequiredAttributes(
            revision,
            configuration,
            configuredKind,
            relation => relation.RequiredForPublication,
            "LISTING_PUBLICATION_ATTRIBUTE_REQUIRED");
        var errors = revision.ValidateForPublication(
            [configuration.Revision.Definition.Site.DefaultLocale],
            evaluatedAtUtc);
        if (!errors.IsDefaultOrEmpty)
        {
            throw new CatalogCommandException(
                "Catalog.PublicationEligibility",
                "LISTING_PUBLICATION_INELIGIBLE",
                422,
                string.Join(" ", errors),
                "Correct the exact draft revision and submit it through editorial review again.",
                new Dictionary<string, object?>
                {
                    ["listingId"] = listing.Id.Value,
                    ["listingRevisionId"] = revision.Id.Value,
                    ["errors"] = errors.ToArray(),
                });
        }
    }

    private static void ValidateExactOwnerIdentities(
        Listing listing,
        ListingRevision revision,
        ProductConfigurationRevisionEnvelope configuration)
    {
        if (listing.CatalogId != configuration.CatalogId)
        {
            throw InvalidConfiguration("LISTING_CATALOG_MISMATCH", "The listing and product configuration belong to different catalogs.");
        }

        if (revision.ListingId != listing.Id)
        {
            throw InvalidConfiguration("LISTING_REVISION_OWNER_MISMATCH", "The listing revision belongs to a different listing.");
        }

        if (revision.ProductConfigurationRevisionId != configuration.Revision.Id
            || revision.TaxonomyRevisionId != configuration.TaxonomyRevisionId
            || revision.AttributeRevisionId != configuration.AttributeRevisionId
            || revision.MarketAreaRevisionId != configuration.MarketAreaRevisionId)
        {
            throw InvalidConfiguration(
                "LISTING_CONFIGURATION_REVISION_MISMATCH",
                "The listing revision does not reference the exact configuration, taxonomy, attribute, and market-area revisions.");
        }
    }

    private static void ValidateRequiredAttributes(
        ListingRevision revision,
        ProductConfigurationRevisionEnvelope configuration,
        ConfiguredListingKind listingKind,
        Func<CategoryAttributeDefinition, bool> isRequired,
        string errorCode)
    {
        var provided = revision.Attributes.Select(item => item.AttributeKey).ToHashSet(StringComparer.Ordinal);
        var selectedCategories = revision.CategoryKeys.ToHashSet(StringComparer.Ordinal);
        foreach (var relation in configuration.Revision.Definition.CategoryAttributes)
        {
            if (selectedCategories.Contains(relation.CategoryKey)
                && relation.AllowedListingKinds.Contains(listingKind)
                && isRequired(relation)
                && !provided.Contains(relation.AttributeKey))
            {
                throw InvalidConfiguration(
                    errorCode,
                    $"Attribute '{relation.AttributeKey}' is required for category '{relation.CategoryKey}'.");
            }
        }
    }

    private static void ValidateOptions(ListingAttributeValue value, IReadOnlyCollection<string> allowedOptions)
    {
        if (value.State is AttributeValueState.Unknown
            or AttributeValueState.NotDisclosed
            or AttributeValueState.NotApplicable)
        {
            return;
        }

        var payload = value.Value;
        if (value.DataType == AttributeDataType.SingleOption && payload is { ValueKind: JsonValueKind.String })
        {
            var selected = payload.Value.GetString()!;
            if (!allowedOptions.Contains(selected, StringComparer.Ordinal))
            {
                throw InvalidConfiguration(
                    "LISTING_ATTRIBUTE_OPTION_INVALID",
                    $"Option '{selected}' is not allowed for attribute '{value.AttributeKey}'.");
            }
        }

        if (value.DataType == AttributeDataType.MultiOption && payload is { ValueKind: JsonValueKind.Array })
        {
            foreach (var selected in payload.Value.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!allowedOptions.Contains(selected, StringComparer.Ordinal))
                {
                    throw InvalidConfiguration(
                        "LISTING_ATTRIBUTE_OPTION_INVALID",
                        $"Option '{selected}' is not allowed for attribute '{value.AttributeKey}'.");
                }
            }
        }
    }

    private static AttributeDataType ToListingType(ConfiguredAttributeDataType value) => value switch
    {
        ConfiguredAttributeDataType.Boolean => AttributeDataType.Boolean,
        ConfiguredAttributeDataType.Integer => AttributeDataType.Integer,
        ConfiguredAttributeDataType.Decimal => AttributeDataType.Decimal,
        ConfiguredAttributeDataType.Money => AttributeDataType.Money,
        ConfiguredAttributeDataType.ShortText => AttributeDataType.ShortText,
        ConfiguredAttributeDataType.LongText => AttributeDataType.LongText,
        ConfiguredAttributeDataType.LocalizedText => AttributeDataType.LocalizedText,
        ConfiguredAttributeDataType.Date => AttributeDataType.Date,
        ConfiguredAttributeDataType.DateTime => AttributeDataType.DateTime,
        ConfiguredAttributeDataType.Duration => AttributeDataType.Duration,
        ConfiguredAttributeDataType.SingleOption => AttributeDataType.SingleOption,
        ConfiguredAttributeDataType.MultiOption => AttributeDataType.MultiOption,
        ConfiguredAttributeDataType.Measurement => AttributeDataType.Measurement,
        ConfiguredAttributeDataType.PhoneCapability => AttributeDataType.PhoneCapability,
        ConfiguredAttributeDataType.ExternalReferenceCapability => AttributeDataType.ExternalReferenceCapability,
        ConfiguredAttributeDataType.GeoClassification => AttributeDataType.GeoClassification,
        _ => throw InvalidConfiguration("ATTRIBUTE_TYPE_UNSUPPORTED", $"Configured attribute type '{value}' is unsupported."),
    };

    private static CatalogCommandException InvalidConfiguration(string code, string message) =>
        new(
            "Catalog.ListingConfiguration",
            code,
            422,
            message,
            "Create a new draft revision against compatible exact product configuration revisions.");
}
