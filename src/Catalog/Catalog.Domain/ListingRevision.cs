using System.Collections.Immutable;

namespace Aggregator.Catalog.Domain;

public enum ProvenanceUsagePolicy
{
    CommercialAllowed = 1,
    ReferenceOnly = 2,
    ResearchOnly = 3,
    Forbidden = 4,
    Unknown = 5,
}

public sealed record LocalizedListingContent(string Locale, string Title, string Summary);

public sealed record ProvenanceReference(
    string FieldPath,
    string SourceKind,
    string SourceReference,
    ProvenanceUsagePolicy UsagePolicy,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ValidUntilUtc);

public sealed class ListingRevision
{
    private ListingRevision(
        ListingRevisionId id,
        ListingId listingId,
        SubjectRevisionId subjectRevisionId,
        ProductConfigurationRevisionId productConfigurationRevisionId,
        TaxonomyRevisionId taxonomyRevisionId,
        AttributeRevisionId attributeRevisionId,
        MarketAreaRevisionId marketAreaRevisionId,
        ImmutableArray<LocalizedListingContent> translations,
        ImmutableArray<string> categoryKeys,
        ImmutableArray<ListingAttributeValue> attributes,
        ImmutableArray<ProvenanceReference> provenance,
        string contentDigest,
        ActorId createdBy,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        ListingId = listingId;
        SubjectRevisionId = subjectRevisionId;
        ProductConfigurationRevisionId = productConfigurationRevisionId;
        TaxonomyRevisionId = taxonomyRevisionId;
        AttributeRevisionId = attributeRevisionId;
        MarketAreaRevisionId = marketAreaRevisionId;
        Translations = translations;
        CategoryKeys = categoryKeys;
        Attributes = attributes;
        Provenance = provenance;
        ContentDigest = contentDigest;
        CreatedBy = createdBy;
        CreatedAtUtc = createdAtUtc;
    }

    public ListingRevisionId Id { get; }

    public ListingId ListingId { get; }

    public SubjectRevisionId SubjectRevisionId { get; }

    public ProductConfigurationRevisionId ProductConfigurationRevisionId { get; }

    public TaxonomyRevisionId TaxonomyRevisionId { get; }

    public AttributeRevisionId AttributeRevisionId { get; }

    public MarketAreaRevisionId MarketAreaRevisionId { get; }

    public ImmutableArray<LocalizedListingContent> Translations { get; }

    public ImmutableArray<string> CategoryKeys { get; }

    public ImmutableArray<ListingAttributeValue> Attributes { get; }

    public ImmutableArray<ProvenanceReference> Provenance { get; }

    public string ContentDigest { get; }

    public ActorId CreatedBy { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static ListingRevision Create(
        ListingRevisionId id,
        ListingId listingId,
        SubjectRevisionId subjectRevisionId,
        ProductConfigurationRevisionId productConfigurationRevisionId,
        TaxonomyRevisionId taxonomyRevisionId,
        AttributeRevisionId attributeRevisionId,
        MarketAreaRevisionId marketAreaRevisionId,
        IEnumerable<LocalizedListingContent> translations,
        IEnumerable<string> categoryKeys,
        IEnumerable<ListingAttributeValue> attributes,
        IEnumerable<ProvenanceReference> provenance,
        string contentDigest,
        ActorId createdBy,
        DateTimeOffset createdAtUtc)
    {
        ValidateIdentifiers(
            id,
            listingId,
            subjectRevisionId,
            productConfigurationRevisionId,
            taxonomyRevisionId,
            attributeRevisionId,
            marketAreaRevisionId,
            createdBy);
        ArgumentNullException.ThrowIfNull(translations);
        ArgumentNullException.ThrowIfNull(categoryKeys);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(provenance);
        CatalogTextRules.RequireDigest(contentDigest, nameof(contentDigest));
        CatalogTextRules.RequireUtc(createdAtUtc, nameof(createdAtUtc));

        var translationArray = translations.OrderBy(item => item.Locale, StringComparer.Ordinal).ToImmutableArray();
        if (translationArray.IsDefaultOrEmpty)
        {
            throw new CatalogDomainException("LISTING_TRANSLATION_REQUIRED", "A listing revision requires localized content.");
        }

        var locales = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var translation in translationArray)
        {
            CatalogTextRules.RequireLocale(translation.Locale, nameof(translation.Locale));
            CatalogTextRules.RequireText(translation.Title, nameof(translation.Title), 300);
            CatalogTextRules.RequireText(translation.Summary, nameof(translation.Summary), 5_000);
            if (!locales.Add(translation.Locale))
            {
                throw new CatalogDomainException("LISTING_LOCALE_DUPLICATE", $"Locale '{translation.Locale}' is duplicated.");
            }
        }

        var categoryArray = categoryKeys.OrderBy(item => item, StringComparer.Ordinal).ToImmutableArray();
        if (categoryArray.IsDefaultOrEmpty)
        {
            throw new CatalogDomainException("LISTING_CATEGORY_REQUIRED", "A listing revision requires at least one category.");
        }

        var categorySet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var categoryKey in categoryArray)
        {
            CatalogTextRules.RequireKey(categoryKey, nameof(categoryKeys));
            if (!categorySet.Add(categoryKey))
            {
                throw new CatalogDomainException("LISTING_CATEGORY_DUPLICATE", $"Category '{categoryKey}' is duplicated.");
            }
        }

        var attributeArray = attributes.OrderBy(item => item.AttributeKey, StringComparer.Ordinal).ToImmutableArray();
        var attributeKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in attributeArray)
        {
            if (!attributeKeys.Add(attribute.AttributeKey))
            {
                throw new CatalogDomainException("LISTING_ATTRIBUTE_DUPLICATE", $"Attribute '{attribute.AttributeKey}' is duplicated.");
            }
        }

        var provenanceArray = provenance
            .OrderBy(item => item.FieldPath, StringComparer.Ordinal)
            .ThenBy(item => item.SourceKind, StringComparer.Ordinal)
            .ThenBy(item => item.SourceReference, StringComparer.Ordinal)
            .ToImmutableArray();
        if (provenanceArray.IsDefaultOrEmpty)
        {
            throw new CatalogDomainException("LISTING_PROVENANCE_REQUIRED", "A listing revision requires field-level provenance.");
        }

        foreach (var reference in provenanceArray)
        {
            CatalogTextRules.RequireText(reference.FieldPath, nameof(reference.FieldPath), 500);
            CatalogTextRules.RequireKey(reference.SourceKind, nameof(reference.SourceKind));
            CatalogTextRules.RequireText(reference.SourceReference, nameof(reference.SourceReference), 1_000);
            CatalogTextRules.RequireUtc(reference.ObservedAtUtc, nameof(reference.ObservedAtUtc));
            if (reference.ValidUntilUtc is { } validUntil)
            {
                CatalogTextRules.RequireUtc(validUntil, nameof(reference.ValidUntilUtc));
                if (validUntil < reference.ObservedAtUtc)
                {
                    throw new CatalogDomainException("PROVENANCE_VALIDITY_INVALID", "Provenance validity cannot end before observation.");
                }
            }
        }

        return new ListingRevision(
            id,
            listingId,
            subjectRevisionId,
            productConfigurationRevisionId,
            taxonomyRevisionId,
            attributeRevisionId,
            marketAreaRevisionId,
            translationArray,
            categoryArray,
            attributeArray,
            provenanceArray,
            contentDigest,
            createdBy,
            createdAtUtc);
    }

    public ImmutableArray<string> ValidateForPublication(
        IEnumerable<string> requiredLocales,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(requiredLocales);
        CatalogTextRules.RequireUtc(evaluatedAtUtc, nameof(evaluatedAtUtc));
        var errors = ImmutableArray.CreateBuilder<string>();
        var translationLocales = Translations.Select(item => item.Locale).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var locale in requiredLocales)
        {
            if (!translationLocales.Contains(locale))
            {
                errors.Add($"Required locale '{locale}' is missing.");
            }
        }

        var provenancePaths = Provenance
            .Where(item => item.UsagePolicy == ProvenanceUsagePolicy.CommercialAllowed
                && (item.ValidUntilUtc is null || item.ValidUntilUtc >= evaluatedAtUtc))
            .Select(item => item.FieldPath)
            .ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var translation in Translations)
        {
            RequireCoverage($"translations/{translation.Locale}/title", provenancePaths, errors);
            RequireCoverage($"translations/{translation.Locale}/summary", provenancePaths, errors);
        }

        foreach (var category in CategoryKeys)
        {
            RequireCoverage($"categories/{category}", provenancePaths, errors);
        }

        foreach (var attribute in Attributes.Where(item => item.State is not AttributeValueState.Unknown
                     and not AttributeValueState.NotDisclosed
                     and not AttributeValueState.NotApplicable))
        {
            RequireCoverage($"attributes/{attribute.AttributeKey}", provenancePaths, errors);
        }

        foreach (var reference in Provenance)
        {
            if (reference.UsagePolicy is ProvenanceUsagePolicy.Forbidden
                or ProvenanceUsagePolicy.ResearchOnly
                or ProvenanceUsagePolicy.Unknown)
            {
                errors.Add($"Field '{reference.FieldPath}' has non-publishable usage policy '{reference.UsagePolicy}'.");
            }
        }

        return errors.ToImmutable();
    }

    private static void RequireCoverage(
        string path,
        ImmutableHashSet<string> provenancePaths,
        ImmutableArray<string>.Builder errors)
    {
        if (!provenancePaths.Contains(path))
        {
            errors.Add($"Field '{path}' lacks current commercial provenance.");
        }
    }

    private static void ValidateIdentifiers(
        ListingRevisionId id,
        ListingId listingId,
        SubjectRevisionId subjectRevisionId,
        ProductConfigurationRevisionId productConfigurationRevisionId,
        TaxonomyRevisionId taxonomyRevisionId,
        AttributeRevisionId attributeRevisionId,
        MarketAreaRevisionId marketAreaRevisionId,
        ActorId createdBy)
    {
        CatalogTextRules.RequireIdentifier(id.Value, nameof(id));
        CatalogTextRules.RequireIdentifier(listingId.Value, nameof(listingId));
        CatalogTextRules.RequireIdentifier(subjectRevisionId.Value, nameof(subjectRevisionId));
        CatalogTextRules.RequireIdentifier(productConfigurationRevisionId.Value, nameof(productConfigurationRevisionId));
        CatalogTextRules.RequireIdentifier(taxonomyRevisionId.Value, nameof(taxonomyRevisionId));
        CatalogTextRules.RequireIdentifier(attributeRevisionId.Value, nameof(attributeRevisionId));
        CatalogTextRules.RequireIdentifier(marketAreaRevisionId.Value, nameof(marketAreaRevisionId));
        CatalogTextRules.RequireIdentifier(createdBy.Value, nameof(createdBy));
    }
}
