using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aggregator.Catalog.Domain;

public sealed record SiteKey
{
    private SiteKey(string value) => Value = value;

    public string Value { get; }

    public static SiteKey Create(string value) => new(CatalogIdentifier.RequireKey(value, nameof(value)));

    public override string ToString() => Value;
}

public sealed record CatalogKey
{
    private CatalogKey(string value) => Value = value;

    public string Value { get; }

    public static CatalogKey Create(string value) => new(CatalogIdentifier.RequireKey(value, nameof(value)));

    public override string ToString() => Value;
}

public sealed record CategoryKey
{
    private CategoryKey(string value) => Value = value;

    public string Value { get; }

    public static CategoryKey Create(string value) => new(CatalogIdentifier.RequireKey(value, nameof(value)));

    public override string ToString() => Value;
}

public sealed record AttributeKey
{
    private AttributeKey(string value) => Value = value;

    public string Value { get; }

    public static AttributeKey Create(string value) => new(CatalogIdentifier.RequireKey(value, nameof(value)));

    public override string ToString() => Value;
}

public sealed record LocaleCode
{
    private LocaleCode(string value) => Value = value;

    public string Value { get; }

    public static LocaleCode Create(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        try
        {
            var culture = CultureInfo.GetCultureInfo(value.Trim());
            return new LocaleCode(culture.Name);
        }
        catch (CultureNotFoundException exception)
        {
            throw new ArgumentException($"'{value}' is not a valid locale code.", nameof(value), exception);
        }
    }

    public override string ToString() => Value;
}

public enum SubjectKind
{
    Organization = 1,
    Place = 2,
    Provider = 3,
}

public enum AttributeValueKind
{
    Boolean = 1,
    Decimal = 2,
    Text = 3,
    TextSet = 4,
    DurationMinutes = 5,
}

public enum AttributeCardinality
{
    Single = 1,
    Multiple = 2,
}

public enum PublicFieldRequirement
{
    Optional = 1,
    RequiredForPublication = 2,
}

public sealed record SiteDefinition
{
    private SiteDefinition(
        SiteKey key,
        LocaleCode defaultLocale,
        IReadOnlySet<LocaleCode> supportedLocales,
        string currency,
        string timeZone)
    {
        Key = key;
        DefaultLocale = defaultLocale;
        SupportedLocales = supportedLocales;
        Currency = currency;
        TimeZone = timeZone;
    }

    public SiteKey Key { get; }

    public LocaleCode DefaultLocale { get; }

    public IReadOnlySet<LocaleCode> SupportedLocales { get; }

    public string Currency { get; }

    public string TimeZone { get; }

    public static SiteDefinition Create(
        SiteKey key,
        LocaleCode defaultLocale,
        IEnumerable<LocaleCode> supportedLocales,
        string currency,
        string timeZone)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(defaultLocale);
        ArgumentNullException.ThrowIfNull(supportedLocales);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);

        var locales = supportedLocales.ToHashSet();
        if (locales.Count == 0)
        {
            throw new ArgumentException("A site must support at least one locale.", nameof(supportedLocales));
        }

        if (!locales.Contains(defaultLocale))
        {
            throw new ArgumentException("The default locale must be part of supported locales.", nameof(defaultLocale));
        }

        var normalizedCurrency = currency.Trim().ToUpperInvariant();
        if (normalizedCurrency.Length != 3 || normalizedCurrency.Any(character => character is < 'A' or > 'Z'))
        {
            throw new ArgumentException("Currency must be a three-letter ISO-style code.", nameof(currency));
        }

        try
        {
            _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new ArgumentException($"Unknown time zone '{timeZone}'.", nameof(timeZone), exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new ArgumentException($"Invalid time zone '{timeZone}'.", nameof(timeZone), exception);
        }

        return new SiteDefinition(
            key,
            defaultLocale,
            new ReadOnlySet<LocaleCode>(locales),
            normalizedCurrency,
            timeZone.Trim());
    }
}

public sealed record CatalogDefinition
{
    private CatalogDefinition(
        CatalogKey key,
        SiteKey siteKey,
        string marketAreaKey,
        string currency,
        string timeZone,
        IReadOnlySet<SubjectKind> allowedListingKinds)
    {
        Key = key;
        SiteKey = siteKey;
        MarketAreaKey = marketAreaKey;
        Currency = currency;
        TimeZone = timeZone;
        AllowedListingKinds = allowedListingKinds;
    }

    public CatalogKey Key { get; }

    public SiteKey SiteKey { get; }

    public string MarketAreaKey { get; }

    public string Currency { get; }

    public string TimeZone { get; }

    public IReadOnlySet<SubjectKind> AllowedListingKinds { get; }

    public static CatalogDefinition Create(
        CatalogKey key,
        SiteKey siteKey,
        string marketAreaKey,
        string currency,
        string timeZone,
        IEnumerable<SubjectKind> allowedListingKinds)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(siteKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(marketAreaKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        ArgumentNullException.ThrowIfNull(allowedListingKinds);

        var kinds = allowedListingKinds.ToHashSet();
        if (kinds.Count == 0)
        {
            throw new ArgumentException("A catalog must allow at least one public subject kind.", nameof(allowedListingKinds));
        }

        if (kinds.Contains(SubjectKind.Organization))
        {
            throw new ArgumentException("Organization is a subject owner and cannot be a public listing kind.", nameof(allowedListingKinds));
        }

        return new CatalogDefinition(
            key,
            siteKey,
            CatalogIdentifier.RequireKey(marketAreaKey, nameof(marketAreaKey)),
            currency.Trim().ToUpperInvariant(),
            timeZone.Trim(),
            new ReadOnlySet<SubjectKind>(kinds));
    }
}

public sealed record CategoryDefinition
{
    private CategoryDefinition(
        CategoryKey key,
        IReadOnlySet<SubjectKind> subjectKinds,
        IReadOnlyDictionary<LocaleCode, string> localizedNames,
        bool isActive)
    {
        Key = key;
        SubjectKinds = subjectKinds;
        LocalizedNames = localizedNames;
        IsActive = isActive;
    }

    public CategoryKey Key { get; }

    public IReadOnlySet<SubjectKind> SubjectKinds { get; }

    public IReadOnlyDictionary<LocaleCode, string> LocalizedNames { get; }

    public bool IsActive { get; }

    public static CategoryDefinition Create(
        CategoryKey key,
        IEnumerable<SubjectKind> subjectKinds,
        IReadOnlyDictionary<LocaleCode, string> localizedNames,
        bool isActive)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(subjectKinds);
        ArgumentNullException.ThrowIfNull(localizedNames);

        var kinds = subjectKinds.ToHashSet();
        if (kinds.Count == 0 || kinds.Contains(SubjectKind.Organization))
        {
            throw new ArgumentException("A category must target one or more public listing kinds.", nameof(subjectKinds));
        }

        var names = NormalizeLocalizedText(localizedNames, nameof(localizedNames));
        return new CategoryDefinition(
            key,
            new ReadOnlySet<SubjectKind>(kinds),
            new ReadOnlyDictionary<LocaleCode, string>(names),
            isActive);
    }

    private static Dictionary<LocaleCode, string> NormalizeLocalizedText(
        IReadOnlyDictionary<LocaleCode, string> values,
        string parameterName)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("At least one localized value is required.", parameterName);
        }

        var result = new Dictionary<LocaleCode, string>();
        foreach (var (locale, value) in values)
        {
            ArgumentNullException.ThrowIfNull(locale);
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            result.Add(locale, value.Trim());
        }

        return result;
    }
}

public sealed record AttributeDefinition
{
    private AttributeDefinition(
        AttributeKey key,
        AttributeValueKind valueKind,
        AttributeCardinality cardinality,
        PublicFieldRequirement requirement,
        IReadOnlySet<CategoryKey> categories,
        IReadOnlyDictionary<LocaleCode, string> localizedNames,
        decimal? minimum,
        decimal? maximum,
        IReadOnlySet<string> allowedValues,
        bool isFilterable,
        bool isSortable)
    {
        Key = key;
        ValueKind = valueKind;
        Cardinality = cardinality;
        Requirement = requirement;
        Categories = categories;
        LocalizedNames = localizedNames;
        Minimum = minimum;
        Maximum = maximum;
        AllowedValues = allowedValues;
        IsFilterable = isFilterable;
        IsSortable = isSortable;
    }

    public AttributeKey Key { get; }

    public AttributeValueKind ValueKind { get; }

    public AttributeCardinality Cardinality { get; }

    public PublicFieldRequirement Requirement { get; }

    public IReadOnlySet<CategoryKey> Categories { get; }

    public IReadOnlyDictionary<LocaleCode, string> LocalizedNames { get; }

    public decimal? Minimum { get; }

    public decimal? Maximum { get; }

    public IReadOnlySet<string> AllowedValues { get; }

    public bool IsFilterable { get; }

    public bool IsSortable { get; }

    public static AttributeDefinition Create(
        AttributeKey key,
        AttributeValueKind valueKind,
        AttributeCardinality cardinality,
        PublicFieldRequirement requirement,
        IEnumerable<CategoryKey> categories,
        IReadOnlyDictionary<LocaleCode, string> localizedNames,
        decimal? minimum,
        decimal? maximum,
        IEnumerable<string>? allowedValues,
        bool isFilterable,
        bool isSortable)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(localizedNames);

        var categorySet = categories.ToHashSet();
        if (categorySet.Count == 0)
        {
            throw new ArgumentException("An attribute must be bound to at least one category.", nameof(categories));
        }

        if (minimum is not null && maximum is not null && minimum > maximum)
        {
            throw new ArgumentException("Attribute minimum cannot exceed maximum.");
        }

        if (valueKind is not AttributeValueKind.Decimal and not AttributeValueKind.DurationMinutes &&
            (minimum is not null || maximum is not null))
        {
            throw new ArgumentException("Only numeric and duration attributes can define bounds.");
        }

        var normalizedAllowedValues = (allowedValues ?? [])
            .Select(value => CatalogIdentifier.RequireKey(value, nameof(allowedValues)))
            .ToHashSet(StringComparer.Ordinal);

        if (normalizedAllowedValues.Count > 0 && valueKind is not AttributeValueKind.Text and not AttributeValueKind.TextSet)
        {
            throw new ArgumentException("Allowed values apply only to text attributes.", nameof(allowedValues));
        }

        if (cardinality == AttributeCardinality.Multiple && valueKind != AttributeValueKind.TextSet)
        {
            throw new ArgumentException("Multiple cardinality requires the text-set value kind.", nameof(cardinality));
        }

        if (isSortable && valueKind == AttributeValueKind.TextSet)
        {
            throw new ArgumentException("Set-valued attributes cannot be sortable.", nameof(isSortable));
        }

        var names = new Dictionary<LocaleCode, string>();
        foreach (var (locale, value) in localizedNames)
        {
            ArgumentNullException.ThrowIfNull(locale);
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            names.Add(locale, value.Trim());
        }

        if (names.Count == 0)
        {
            throw new ArgumentException("At least one localized attribute name is required.", nameof(localizedNames));
        }

        return new AttributeDefinition(
            key,
            valueKind,
            cardinality,
            requirement,
            new ReadOnlySet<CategoryKey>(categorySet),
            new ReadOnlyDictionary<LocaleCode, string>(names),
            minimum,
            maximum,
            new ReadOnlySet<string>(normalizedAllowedValues),
            isFilterable,
            isSortable);
    }
}

public sealed class ProductConfiguration
{
    private ProductConfiguration(
        Guid revisionId,
        string digest,
        SiteDefinition site,
        CatalogDefinition catalog,
        IReadOnlyDictionary<CategoryKey, CategoryDefinition> categories,
        IReadOnlyDictionary<AttributeKey, AttributeDefinition> attributes,
        DateTimeOffset createdAtUtc)
    {
        RevisionId = revisionId;
        Digest = digest;
        Site = site;
        Catalog = catalog;
        Categories = categories;
        Attributes = attributes;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid RevisionId { get; }

    public string Digest { get; }

    public SiteDefinition Site { get; }

    public CatalogDefinition Catalog { get; }

    public IReadOnlyDictionary<CategoryKey, CategoryDefinition> Categories { get; }

    public IReadOnlyDictionary<AttributeKey, AttributeDefinition> Attributes { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static ProductConfiguration Create(
        Guid revisionId,
        string digest,
        SiteDefinition site,
        CatalogDefinition catalog,
        IEnumerable<CategoryDefinition> categories,
        IEnumerable<AttributeDefinition> attributes,
        DateTimeOffset createdAtUtc)
    {
        if (revisionId == Guid.Empty)
        {
            throw new ArgumentException("Configuration revision ID is required.", nameof(revisionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(attributes);

        CatalogClock.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        if (catalog.SiteKey != site.Key)
        {
            throw new ArgumentException("Catalog and site definitions must belong to the same site.", nameof(catalog));
        }

        var categoryMap = categories.ToDictionary(category => category.Key);
        if (categoryMap.Count == 0)
        {
            throw new ArgumentException("A product configuration must define at least one category.", nameof(categories));
        }

        foreach (var category in categoryMap.Values)
        {
            if (!category.LocalizedNames.ContainsKey(site.DefaultLocale))
            {
                throw new ArgumentException($"Category '{category.Key}' lacks the default locale '{site.DefaultLocale}'.", nameof(categories));
            }

            if (!category.SubjectKinds.IsSubsetOf(catalog.AllowedListingKinds))
            {
                throw new ArgumentException($"Category '{category.Key}' targets a listing kind not allowed by the catalog.", nameof(categories));
            }
        }

        var attributeMap = attributes.ToDictionary(attribute => attribute.Key);
        foreach (var attribute in attributeMap.Values)
        {
            if (!attribute.LocalizedNames.ContainsKey(site.DefaultLocale))
            {
                throw new ArgumentException($"Attribute '{attribute.Key}' lacks the default locale '{site.DefaultLocale}'.", nameof(attributes));
            }

            var unknownCategories = attribute.Categories.Where(category => !categoryMap.ContainsKey(category)).ToArray();
            if (unknownCategories.Length > 0)
            {
                throw new ArgumentException(
                    $"Attribute '{attribute.Key}' references unknown categories: {string.Join(", ", unknownCategories)}.",
                    nameof(attributes));
            }
        }

        return new ProductConfiguration(
            revisionId,
            CatalogDigest.RequireSha256(digest, nameof(digest)),
            site,
            catalog,
            new ReadOnlyDictionary<CategoryKey, CategoryDefinition>(categoryMap),
            new ReadOnlyDictionary<AttributeKey, AttributeDefinition>(attributeMap),
            createdAtUtc);
    }

    public CategoryDefinition RequireCategory(CategoryKey key, SubjectKind subjectKind)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!Categories.TryGetValue(key, out var category) || !category.IsActive)
        {
            throw new CatalogInvariantException($"Category '{key}' is not active in configuration '{RevisionId}'.");
        }

        if (!category.SubjectKinds.Contains(subjectKind))
        {
            throw new CatalogInvariantException($"Category '{key}' does not support subject kind '{subjectKind}'.");
        }

        return category;
    }

    public AttributeDefinition RequireAttribute(AttributeKey key, IReadOnlySet<CategoryKey> listingCategories)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(listingCategories);

        if (!Attributes.TryGetValue(key, out var attribute))
        {
            throw new CatalogInvariantException($"Attribute '{key}' is not defined in configuration '{RevisionId}'.");
        }

        if (!attribute.Categories.Overlaps(listingCategories))
        {
            throw new CatalogInvariantException($"Attribute '{key}' is not applicable to the listing categories.");
        }

        return attribute;
    }
}

public sealed class CatalogInvariantException : InvalidOperationException
{
    public CatalogInvariantException(string message)
        : base(message)
    {
    }
}

internal static partial class CatalogIdentifier
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    public static string RequireKey(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length > 96 || !KeyRegex().IsMatch(normalized))
        {
            throw new ArgumentException(
                "Keys must contain lowercase ASCII letters, digits, and single hyphen separators.",
                parameterName);
        }

        return normalized;
    }
}

internal static class CatalogClock
{
    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be expressed in UTC.", parameterName);
        }
    }
}

internal static class CatalogDigest
{
    public static string RequireSha256(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new ArgumentException("Digest must be a lowercase SHA-256 hexadecimal value.", parameterName);
        }

        return normalized;
    }
}
