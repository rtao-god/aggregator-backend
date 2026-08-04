using System.Collections.Immutable;

namespace Aggregator.Catalog.Domain;

public enum ConfiguredListingKind
{
    Place = 1,
    Provider = 2,
}

public enum ConfiguredAttributeDataType
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

public sealed record SiteConfigurationDefinition(
    string Key,
    string DefaultLocale,
    ImmutableArray<string> SupportedLocales,
    string DefaultCurrency,
    string TimeZone,
    string BrandKey,
    ImmutableArray<string> HostMappings,
    ImmutableDictionary<string, string> LegalPageReferences);

public sealed record CatalogConfigurationDefinition(
    string Key,
    string SiteKey,
    ImmutableDictionary<string, string> Titles,
    string MarketAreaKey,
    string Currency,
    string TimeZone,
    ImmutableHashSet<ConfiguredListingKind> SupportedListingKinds,
    string SeoPolicyKey,
    string PublicationPolicyKey,
    string ContactPolicyKey,
    string ClaimPolicyKey,
    string PromotionEligibilityPolicyKey);

public sealed record CategoryDefinition(
    string Key,
    string? ParentKey,
    ImmutableDictionary<string, string> Names,
    ImmutableDictionary<string, string> Slugs,
    ImmutableHashSet<ConfiguredListingKind> AllowedListingKinds,
    bool PrimaryAllowed,
    bool SeoIndexable,
    int SortOrder);

public sealed record AttributeDefinition(
    string Key,
    ConfiguredAttributeDataType DataType,
    bool Multiple,
    bool Filterable,
    bool Comparable,
    bool Sortable,
    bool Public,
    ImmutableArray<string> AllowedOptions,
    ImmutableDictionary<string, string> Labels);

public sealed record CategoryAttributeDefinition(
    string CategoryKey,
    string AttributeKey,
    bool RequiredForDraft,
    bool RequiredForPublication,
    bool FilterableInCategory,
    bool Comparable,
    bool VisibleInCard,
    ImmutableHashSet<ConfiguredListingKind> AllowedListingKinds,
    string DisplayGroup,
    int DisplayOrder);

public sealed class ProductConfigurationDefinition
{
    private ProductConfigurationDefinition(
        SiteConfigurationDefinition site,
        CatalogConfigurationDefinition catalog,
        ImmutableArray<CategoryDefinition> categories,
        ImmutableArray<AttributeDefinition> attributes,
        ImmutableArray<CategoryAttributeDefinition> categoryAttributes)
    {
        Site = site;
        Catalog = catalog;
        Categories = categories;
        Attributes = attributes;
        CategoryAttributes = categoryAttributes;
    }

    public SiteConfigurationDefinition Site { get; }

    public CatalogConfigurationDefinition Catalog { get; }

    public ImmutableArray<CategoryDefinition> Categories { get; }

    public ImmutableArray<AttributeDefinition> Attributes { get; }

    public ImmutableArray<CategoryAttributeDefinition> CategoryAttributes { get; }

    public static ProductConfigurationDefinition Create(
        SiteConfigurationDefinition site,
        CatalogConfigurationDefinition catalog,
        IEnumerable<CategoryDefinition> categories,
        IEnumerable<AttributeDefinition> attributes,
        IEnumerable<CategoryAttributeDefinition> categoryAttributes)
    {
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(categoryAttributes);

        ValidateSite(site);
        ValidateCatalog(site, catalog);
        var categoryArray = categories.OrderBy(item => item.Key, StringComparer.Ordinal).ToImmutableArray();
        var attributeArray = attributes.OrderBy(item => item.Key, StringComparer.Ordinal).ToImmutableArray();
        var relationArray = categoryAttributes
            .OrderBy(item => item.CategoryKey, StringComparer.Ordinal)
            .ThenBy(item => item.DisplayOrder)
            .ThenBy(item => item.AttributeKey, StringComparer.Ordinal)
            .ToImmutableArray();
        ValidateCategories(site, catalog, categoryArray);
        ValidateAttributes(site, attributeArray);
        ValidateCategoryAttributes(catalog, categoryArray, attributeArray, relationArray);
        return new ProductConfigurationDefinition(site, catalog, categoryArray, attributeArray, relationArray);
    }

    private static void ValidateSite(SiteConfigurationDefinition site)
    {
        CatalogTextRules.RequireKey(site.Key, nameof(site.Key));
        CatalogTextRules.RequireLocale(site.DefaultLocale, nameof(site.DefaultLocale));
        if (site.SupportedLocales.IsDefaultOrEmpty)
        {
            throw new CatalogDomainException("SITE_LOCALES_REQUIRED", "A site must support at least one locale.");
        }

        var locales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var locale in site.SupportedLocales)
        {
            CatalogTextRules.RequireLocale(locale, nameof(site.SupportedLocales));
            if (!locales.Add(locale))
            {
                throw new CatalogDomainException("SITE_LOCALE_DUPLICATE", $"Locale '{locale}' is duplicated.");
            }
        }

        if (!locales.Contains(site.DefaultLocale))
        {
            throw new CatalogDomainException("SITE_DEFAULT_LOCALE_UNSUPPORTED", "The default locale must be included in supported locales.");
        }

        ValidateCurrency(site.DefaultCurrency, nameof(site.DefaultCurrency));
        CatalogTextRules.RequireText(site.TimeZone, nameof(site.TimeZone), 100);
        CatalogTextRules.RequireKey(site.BrandKey, nameof(site.BrandKey));
        if (site.HostMappings.IsDefaultOrEmpty)
        {
            throw new CatalogDomainException("SITE_HOST_REQUIRED", "A site must declare at least one host mapping.");
        }

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in site.HostMappings)
        {
            CatalogTextRules.RequireText(host, nameof(site.HostMappings), 253);
            if (Uri.CheckHostName(host) == UriHostNameType.Unknown || !hosts.Add(host))
            {
                throw new CatalogDomainException("SITE_HOST_INVALID", $"Host mapping '{host}' is invalid or duplicated.");
            }
        }

        if (site.LegalPageReferences.IsEmpty)
        {
            throw new CatalogDomainException("SITE_LEGAL_REFERENCE_REQUIRED", "A site must declare legal page references.");
        }
    }

    private static void ValidateCatalog(SiteConfigurationDefinition site, CatalogConfigurationDefinition catalog)
    {
        CatalogTextRules.RequireKey(catalog.Key, nameof(catalog.Key));
        CatalogTextRules.RequireKey(catalog.SiteKey, nameof(catalog.SiteKey));
        if (!string.Equals(site.Key, catalog.SiteKey, StringComparison.Ordinal))
        {
            throw new CatalogDomainException("CATALOG_SITE_MISMATCH", "The catalog must reference the site in the same configuration artifact.");
        }

        ValidateLocalizedValues(catalog.Titles, site.SupportedLocales, "catalog title");
        CatalogTextRules.RequireKey(catalog.MarketAreaKey, nameof(catalog.MarketAreaKey));
        ValidateCurrency(catalog.Currency, nameof(catalog.Currency));
        CatalogTextRules.RequireText(catalog.TimeZone, nameof(catalog.TimeZone), 100);
        if (catalog.SupportedListingKinds.IsEmpty)
        {
            throw new CatalogDomainException("CATALOG_LISTING_KIND_REQUIRED", "A catalog must support at least one listing kind.");
        }

        CatalogTextRules.RequireKey(catalog.SeoPolicyKey, nameof(catalog.SeoPolicyKey));
        CatalogTextRules.RequireKey(catalog.PublicationPolicyKey, nameof(catalog.PublicationPolicyKey));
        CatalogTextRules.RequireKey(catalog.ContactPolicyKey, nameof(catalog.ContactPolicyKey));
        CatalogTextRules.RequireKey(catalog.ClaimPolicyKey, nameof(catalog.ClaimPolicyKey));
        CatalogTextRules.RequireKey(catalog.PromotionEligibilityPolicyKey, nameof(catalog.PromotionEligibilityPolicyKey));
    }

    private static void ValidateCategories(
        SiteConfigurationDefinition site,
        CatalogConfigurationDefinition catalog,
        ImmutableArray<CategoryDefinition> categories)
    {
        if (categories.IsDefaultOrEmpty)
        {
            throw new CatalogDomainException("CATEGORY_REQUIRED", "A catalog must declare at least one category.");
        }

        var byKey = new Dictionary<string, CategoryDefinition>(StringComparer.Ordinal);
        var slugs = new HashSet<(string Locale, string Slug)>();
        foreach (var category in categories)
        {
            CatalogTextRules.RequireKey(category.Key, nameof(category.Key));
            if (!byKey.TryAdd(category.Key, category))
            {
                throw new CatalogDomainException("CATEGORY_KEY_DUPLICATE", $"Category key '{category.Key}' is duplicated.");
            }

            if (category.ParentKey is not null)
            {
                CatalogTextRules.RequireKey(category.ParentKey, nameof(category.ParentKey));
            }

            ValidateLocalizedValues(category.Names, site.SupportedLocales, $"category '{category.Key}' name");
            ValidateLocalizedValues(category.Slugs, site.SupportedLocales, $"category '{category.Key}' slug");
            foreach (var slug in category.Slugs)
            {
                CatalogTextRules.RequireKey(slug.Value, $"category '{category.Key}' slug");
                if (!slugs.Add((slug.Key.ToUpperInvariant(), slug.Value)))
                {
                    throw new CatalogDomainException(
                        "CATEGORY_SLUG_DUPLICATE",
                        $"Category slug '{slug.Value}' is duplicated for locale '{slug.Key}'.");
                }
            }

            if (category.AllowedListingKinds.IsEmpty || !category.AllowedListingKinds.IsSubsetOf(catalog.SupportedListingKinds))
            {
                throw new CatalogDomainException(
                    "CATEGORY_LISTING_KIND_INVALID",
                    $"Category '{category.Key}' must allow a non-empty subset of the catalog listing kinds.");
            }
        }

        foreach (var category in categories)
        {
            if (category.ParentKey is not null && !byKey.ContainsKey(category.ParentKey))
            {
                throw new CatalogDomainException(
                    "CATEGORY_PARENT_UNKNOWN",
                    $"Category '{category.Key}' references unknown parent '{category.ParentKey}'.");
            }

            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = category;
            while (current.ParentKey is not null)
            {
                if (!visited.Add(current.Key))
                {
                    throw new CatalogDomainException("CATEGORY_HIERARCHY_CYCLE", $"Category hierarchy contains a cycle at '{current.Key}'.");
                }

                current = byKey[current.ParentKey];
            }
        }
    }

    private static void ValidateAttributes(
        SiteConfigurationDefinition site,
        ImmutableArray<AttributeDefinition> attributes)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributes)
        {
            CatalogTextRules.RequireKey(attribute.Key, nameof(attribute.Key));
            if (!keys.Add(attribute.Key))
            {
                throw new CatalogDomainException("ATTRIBUTE_KEY_DUPLICATE", $"Attribute key '{attribute.Key}' is duplicated.");
            }

            ValidateLocalizedValues(attribute.Labels, site.SupportedLocales, $"attribute '{attribute.Key}' label");
            var optionType = attribute.DataType is ConfiguredAttributeDataType.SingleOption or ConfiguredAttributeDataType.MultiOption;
            if (optionType && attribute.AllowedOptions.IsDefaultOrEmpty)
            {
                throw new CatalogDomainException("ATTRIBUTE_OPTIONS_REQUIRED", $"Attribute '{attribute.Key}' requires allowed options.");
            }

            if (!optionType && !attribute.AllowedOptions.IsDefaultOrEmpty)
            {
                throw new CatalogDomainException("ATTRIBUTE_OPTIONS_FORBIDDEN", $"Attribute '{attribute.Key}' cannot declare options for its data type.");
            }

            var options = new HashSet<string>(StringComparer.Ordinal);
            foreach (var option in attribute.AllowedOptions)
            {
                CatalogTextRules.RequireKey(option, nameof(attribute.AllowedOptions));
                if (!options.Add(option))
                {
                    throw new CatalogDomainException("ATTRIBUTE_OPTION_DUPLICATE", $"Option '{option}' is duplicated on attribute '{attribute.Key}'.");
                }
            }
        }
    }

    private static void ValidateCategoryAttributes(
        CatalogConfigurationDefinition catalog,
        ImmutableArray<CategoryDefinition> categories,
        ImmutableArray<AttributeDefinition> attributes,
        ImmutableArray<CategoryAttributeDefinition> categoryAttributes)
    {
        var categoryByKey = categories.ToImmutableDictionary(item => item.Key, StringComparer.Ordinal);
        var attributeKeys = attributes.Select(item => item.Key).ToImmutableHashSet(StringComparer.Ordinal);
        var pairs = new HashSet<(string Category, string Attribute)>();
        foreach (var relation in categoryAttributes)
        {
            if (!categoryByKey.TryGetValue(relation.CategoryKey, out var category))
            {
                throw new CatalogDomainException("CATEGORY_ATTRIBUTE_CATEGORY_UNKNOWN", $"Unknown category '{relation.CategoryKey}'.");
            }

            if (!attributeKeys.Contains(relation.AttributeKey))
            {
                throw new CatalogDomainException("CATEGORY_ATTRIBUTE_ATTRIBUTE_UNKNOWN", $"Unknown attribute '{relation.AttributeKey}'.");
            }

            if (!pairs.Add((relation.CategoryKey, relation.AttributeKey)))
            {
                throw new CatalogDomainException(
                    "CATEGORY_ATTRIBUTE_DUPLICATE",
                    $"Attribute '{relation.AttributeKey}' is assigned to category '{relation.CategoryKey}' more than once.");
            }

            if (relation.AllowedListingKinds.IsEmpty
                || !relation.AllowedListingKinds.IsSubsetOf(category.AllowedListingKinds)
                || !relation.AllowedListingKinds.IsSubsetOf(catalog.SupportedListingKinds))
            {
                throw new CatalogDomainException(
                    "CATEGORY_ATTRIBUTE_LISTING_KIND_INVALID",
                    $"Category-attribute relation '{relation.CategoryKey}/{relation.AttributeKey}' has invalid listing kinds.");
            }

            CatalogTextRules.RequireKey(relation.DisplayGroup, nameof(relation.DisplayGroup));
        }
    }

    private static void ValidateLocalizedValues(
        ImmutableDictionary<string, string> values,
        ImmutableArray<string> requiredLocales,
        string fieldName)
    {
        foreach (var locale in requiredLocales)
        {
            if (!values.TryGetValue(locale, out var value) || string.IsNullOrWhiteSpace(value))
            {
                throw new CatalogDomainException(
                    "LOCALIZED_VALUE_REQUIRED",
                    $"{fieldName} is required for locale '{locale}'.");
            }
        }

        foreach (var value in values)
        {
            CatalogTextRules.RequireLocale(value.Key, nameof(values));
            CatalogTextRules.RequireText(value.Value, fieldName, 500);
        }
    }

    private static void ValidateCurrency(string currency, string parameterName)
    {
        CatalogTextRules.RequireText(currency, parameterName, 3);
        if (currency.Length != 3 || currency.Any(character => !char.IsAsciiLetter(character) || char.IsLower(character)))
        {
            throw new CatalogDomainException("CURRENCY_INVALID", $"'{parameterName}' must be a three-letter uppercase currency code.");
        }
    }
}

public sealed class ProductConfigurationRevision
{
    private ProductConfigurationRevision(
        ProductConfigurationRevisionId id,
        string semanticIdentity,
        string contentDigest,
        string sourceCommitIdentity,
        ActorId createdBy,
        DateTimeOffset createdAtUtc,
        ProductConfigurationDefinition definition)
    {
        Id = id;
        SemanticIdentity = semanticIdentity;
        ContentDigest = contentDigest;
        SourceCommitIdentity = sourceCommitIdentity;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
        Definition = definition;
    }

    public ProductConfigurationRevisionId Id { get; }

    public string SemanticIdentity { get; }

    public string ContentDigest { get; }

    public string SourceCommitIdentity { get; }

    public ActorId CreatedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public ProductConfigurationDefinition Definition { get; }

    public static ProductConfigurationRevision Create(
        ProductConfigurationRevisionId id,
        string semanticIdentity,
        string contentDigest,
        string sourceCommitIdentity,
        ActorId createdBy,
        DateTimeOffset createdAtUtc,
        ProductConfigurationDefinition definition)
    {
        CatalogTextRules.RequireIdentifier(id.Value, nameof(id));
        CatalogTextRules.RequireKey(semanticIdentity, nameof(semanticIdentity));
        CatalogTextRules.RequireDigest(contentDigest, nameof(contentDigest));
        CatalogTextRules.RequireText(sourceCommitIdentity, nameof(sourceCommitIdentity), 200);
        CatalogTextRules.RequireIdentifier(createdBy.Value, nameof(createdBy));
        CatalogTextRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        ArgumentNullException.ThrowIfNull(definition);
        return new ProductConfigurationRevision(
            id,
            semanticIdentity,
            contentDigest,
            sourceCommitIdentity,
            createdBy,
            createdAtUtc,
            definition);
    }
}
