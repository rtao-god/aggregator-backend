using System.Collections.ObjectModel;

namespace Aggregator.Catalog.Domain;

public enum SourceKind
{
    FirstPartySubmission = 1,
    PublicWebsite = 2,
    PublicDirectoryReference = 3,
    EditorialResearch = 4,
    OwnerVerification = 5,
    LicensedDataset = 6,
}

public enum UsagePolicy
{
    PublicAllowed = 1,
    ReferenceOnly = 2,
    ResearchOnly = 3,
    Forbidden = 4,
}

public enum FieldValueState
{
    Observed = 1,
    Missing = 2,
    NotApplicable = 3,
    Withheld = 4,
}

public enum MissingValueReason
{
    NotPublishedBySource = 1,
    NotCollected = 2,
    ConflictingEvidence = 3,
    RightsRestricted = 4,
    OwnerWithheld = 5,
}

public enum ContactKind
{
    Website = 1,
    Email = 2,
    Phone = 3,
    WhatsApp = 4,
    BookingReference = 5,
    MapReference = 6,
}

public enum GeographyState
{
    BerlinCore = 1,
    BerlinNearby = 2,
    RemoteOnly = 3,
    OutsideMarket = 4,
    Unresolved = 5,
}

public enum MediaRightsBasis
{
    OwnerProvided = 1,
    ExplicitLicense = 2,
    OriginalEditorialWork = 3,
    PublicDomain = 4,
}

public sealed record ProvenanceAssertion
{
    private ProvenanceAssertion(
        Guid id,
        SourceKind sourceKind,
        string sourceReference,
        DateTimeOffset observedAtUtc,
        DateTimeOffset recordedAtUtc,
        UsagePolicy usagePolicy,
        string evidenceDigest)
    {
        Id = id;
        SourceKind = sourceKind;
        SourceReference = sourceReference;
        ObservedAtUtc = observedAtUtc;
        RecordedAtUtc = recordedAtUtc;
        UsagePolicy = usagePolicy;
        EvidenceDigest = evidenceDigest;
    }

    public Guid Id { get; }

    public SourceKind SourceKind { get; }

    public string SourceReference { get; }

    public DateTimeOffset ObservedAtUtc { get; }

    public DateTimeOffset RecordedAtUtc { get; }

    public UsagePolicy UsagePolicy { get; }

    public string EvidenceDigest { get; }

    public static ProvenanceAssertion Create(
        Guid id,
        SourceKind sourceKind,
        string sourceReference,
        DateTimeOffset observedAtUtc,
        DateTimeOffset recordedAtUtc,
        UsagePolicy usagePolicy,
        string evidenceDigest)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Assertion ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceReference);
        CatalogClock.RequireUtc(observedAtUtc, nameof(observedAtUtc));
        CatalogClock.RequireUtc(recordedAtUtc, nameof(recordedAtUtc));
        if (observedAtUtc > recordedAtUtc)
        {
            throw new ArgumentException("Observation cannot occur after the assertion is recorded.", nameof(observedAtUtc));
        }

        return new ProvenanceAssertion(
            id,
            sourceKind,
            sourceReference.Trim(),
            observedAtUtc,
            recordedAtUtc,
            usagePolicy,
            CatalogDigest.RequireSha256(evidenceDigest, nameof(evidenceDigest)));
    }

    public void EnsurePublicUseAllowed(string fieldKey)
    {
        if (UsagePolicy != UsagePolicy.PublicAllowed)
        {
            throw new CatalogInvariantException(
                $"Field '{fieldKey}' references assertion '{Id}' with usage policy '{UsagePolicy}'.");
        }
    }
}

public sealed record TypedValue
{
    private TypedValue(
        AttributeValueKind kind,
        bool? booleanValue,
        decimal? decimalValue,
        string? textValue,
        IReadOnlyList<string>? textSetValue)
    {
        Kind = kind;
        BooleanValue = booleanValue;
        DecimalValue = decimalValue;
        TextValue = textValue;
        TextSetValue = textSetValue;
    }

    public AttributeValueKind Kind { get; }

    public bool? BooleanValue { get; }

    public decimal? DecimalValue { get; }

    public string? TextValue { get; }

    public IReadOnlyList<string>? TextSetValue { get; }

    public static TypedValue Boolean(bool value) =>
        new(AttributeValueKind.Boolean, value, null, null, null);

    public static TypedValue Decimal(decimal value) =>
        new(AttributeValueKind.Decimal, null, value, null, null);

    public static TypedValue DurationMinutes(int value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        return new TypedValue(AttributeValueKind.DurationMinutes, null, value, null, null);
    }

    public static TypedValue Text(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new TypedValue(AttributeValueKind.Text, null, null, value.Trim(), null);
    }

    public static TypedValue TextSet(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var normalized = values
            .Select(value => CatalogIdentifier.RequireKey(value, nameof(values)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        if (normalized.Length == 0)
        {
            throw new ArgumentException("A text-set value cannot be empty.", nameof(values));
        }

        return new TypedValue(AttributeValueKind.TextSet, null, null, null, Array.AsReadOnly(normalized));
    }

    public void EnsureMatches(AttributeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (Kind != definition.ValueKind)
        {
            throw new CatalogInvariantException(
                $"Attribute '{definition.Key}' expects '{definition.ValueKind}' but received '{Kind}'.");
        }

        if (Kind is AttributeValueKind.Decimal or AttributeValueKind.DurationMinutes)
        {
            var value = DecimalValue ?? throw new CatalogInvariantException("A numeric value is missing its scalar payload.");
            if (definition.Minimum is not null && value < definition.Minimum)
            {
                throw new CatalogInvariantException($"Attribute '{definition.Key}' is below its configured minimum.");
            }
            if (definition.Maximum is not null && value > definition.Maximum)
            {
                throw new CatalogInvariantException($"Attribute '{definition.Key}' exceeds its configured maximum.");
            }
        }

        if (definition.AllowedValues.Count == 0)
        {
            return;
        }

        var values = Kind == AttributeValueKind.TextSet
            ? TextSetValue ?? throw new CatalogInvariantException("A text-set value is missing its items.")
            : [TextValue ?? throw new CatalogInvariantException("A text value is missing its scalar payload.")];

        var unknownValues = values.Where(value => !definition.AllowedValues.Contains(value)).ToArray();
        if (unknownValues.Length > 0)
        {
            throw new CatalogInvariantException(
                $"Attribute '{definition.Key}' contains values outside its allowlist: {string.Join(", ", unknownValues)}.");
        }
    }
}

public sealed record LocalizedTextValue
{
    private LocalizedTextValue(
        LocaleCode locale,
        FieldValueState state,
        string? value,
        Guid? assertionId,
        MissingValueReason? missingReason)
    {
        Locale = locale;
        State = state;
        Value = value;
        AssertionId = assertionId;
        MissingReason = missingReason;
    }

    public LocaleCode Locale { get; }

    public FieldValueState State { get; }

    public string? Value { get; }

    public Guid? AssertionId { get; }

    public MissingValueReason? MissingReason { get; }

    public static LocalizedTextValue Observed(LocaleCode locale, string value, Guid assertionId)
    {
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (assertionId == Guid.Empty)
        {
            throw new ArgumentException("Assertion ID is required for observed text.", nameof(assertionId));
        }

        return new LocalizedTextValue(locale, FieldValueState.Observed, value.Trim(), assertionId, null);
    }

    public static LocalizedTextValue Missing(LocaleCode locale, MissingValueReason reason)
    {
        ArgumentNullException.ThrowIfNull(locale);
        return new LocalizedTextValue(locale, FieldValueState.Missing, null, null, reason);
    }

    public static LocalizedTextValue Withheld(LocaleCode locale, MissingValueReason reason)
    {
        ArgumentNullException.ThrowIfNull(locale);
        return new LocalizedTextValue(locale, FieldValueState.Withheld, null, null, reason);
    }
}

public sealed record ListingAttributeValue
{
    private ListingAttributeValue(
        AttributeKey key,
        FieldValueState state,
        TypedValue? value,
        Guid? assertionId,
        MissingValueReason? missingReason)
    {
        Key = key;
        State = state;
        Value = value;
        AssertionId = assertionId;
        MissingReason = missingReason;
    }

    public AttributeKey Key { get; }

    public FieldValueState State { get; }

    public TypedValue? Value { get; }

    public Guid? AssertionId { get; }

    public MissingValueReason? MissingReason { get; }

    public static ListingAttributeValue Observed(AttributeKey key, TypedValue value, Guid assertionId)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (assertionId == Guid.Empty)
        {
            throw new ArgumentException("Assertion ID is required for an observed attribute.", nameof(assertionId));
        }

        return new ListingAttributeValue(key, FieldValueState.Observed, value, assertionId, null);
    }

    public static ListingAttributeValue Missing(AttributeKey key, MissingValueReason reason)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new ListingAttributeValue(key, FieldValueState.Missing, null, null, reason);
    }

    public static ListingAttributeValue NotApplicable(AttributeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new ListingAttributeValue(key, FieldValueState.NotApplicable, null, null, null);
    }
}

public sealed record CategoryAssignment(CategoryKey CategoryKey, Guid AssertionId)
{
    public static CategoryAssignment Create(CategoryKey categoryKey, Guid assertionId)
    {
        ArgumentNullException.ThrowIfNull(categoryKey);
        if (assertionId == Guid.Empty)
        {
            throw new ArgumentException("Assertion ID is required for a category assignment.", nameof(assertionId));
        }

        return new CategoryAssignment(categoryKey, assertionId);
    }
}

public sealed record GeographyValue
{
    private GeographyValue(
        GeographyState state,
        decimal? latitude,
        decimal? longitude,
        string? districtKey,
        Guid assertionId)
    {
        State = state;
        Latitude = latitude;
        Longitude = longitude;
        DistrictKey = districtKey;
        AssertionId = assertionId;
    }

    public GeographyState State { get; }

    public decimal? Latitude { get; }

    public decimal? Longitude { get; }

    public string? DistrictKey { get; }

    public Guid AssertionId { get; }

    public static GeographyValue Create(
        GeographyState state,
        decimal? latitude,
        decimal? longitude,
        string? districtKey,
        Guid assertionId)
    {
        if (assertionId == Guid.Empty)
        {
            throw new ArgumentException("Assertion ID is required for geography.", nameof(assertionId));
        }

        if ((latitude is null) != (longitude is null))
        {
            throw new ArgumentException("Latitude and longitude must be supplied together.");
        }

        if (latitude is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(latitude));
        }

        if (longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(longitude));
        }

        if (state == GeographyState.RemoteOnly && (latitude is not null || longitude is not null || districtKey is not null))
        {
            throw new ArgumentException("Remote-only geography cannot include physical coordinates or a district.");
        }

        var normalizedDistrict = districtKey is null
            ? null
            : CatalogIdentifier.RequireKey(districtKey, nameof(districtKey));

        return new GeographyValue(state, latitude, longitude, normalizedDistrict, assertionId);
    }
}

public sealed record ContactValue
{
    private ContactValue(Guid id, ContactKind kind, Uri target, string? label, Guid assertionId)
    {
        Id = id;
        Kind = kind;
        Target = target;
        Label = label;
        AssertionId = assertionId;
    }

    public Guid Id { get; }

    public ContactKind Kind { get; }

    public Uri Target { get; }

    public string? Label { get; }

    public Guid AssertionId { get; }

    public static ContactValue Create(
        Guid id,
        ContactKind kind,
        Uri target,
        string? label,
        Guid assertionId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Contact ID is required.", nameof(id));
        }

        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAbsoluteUri)
        {
            throw new ArgumentException("Contact target must be an absolute URI.", nameof(target));
        }

        if (assertionId == Guid.Empty)
        {
            throw new ArgumentException("Assertion ID is required for a contact.", nameof(assertionId));
        }

        return new ContactValue(
            id,
            kind,
            target,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            assertionId);
    }
}

public sealed record MediaReference
{
    private MediaReference(
        Guid mediaId,
        long mediaAggregateRevision,
        Guid variantId,
        Uri objectUri,
        string contentType,
        string contentDigest,
        MediaRightsBasis rightsBasis,
        int displayOrder,
        string? caption,
        Guid assertionId)
    {
        MediaId = mediaId;
        MediaAggregateRevision = mediaAggregateRevision;
        VariantId = variantId;
        ObjectUri = objectUri;
        ContentType = contentType;
        ContentDigest = contentDigest;
        RightsBasis = rightsBasis;
        DisplayOrder = displayOrder;
        Caption = caption;
        AssertionId = assertionId;
    }

    public Guid MediaId { get; }

    public long MediaAggregateRevision { get; }

    public Guid VariantId { get; }

    public Uri ObjectUri { get; }

    public string ContentType { get; }

    public string ContentDigest { get; }

    public MediaRightsBasis RightsBasis { get; }

    public int DisplayOrder { get; }

    public string? Caption { get; }

    public Guid AssertionId { get; }

    public static MediaReference Create(
        Guid mediaId,
        long mediaAggregateRevision,
        Guid variantId,
        Uri objectUri,
        string contentType,
        string contentDigest,
        MediaRightsBasis rightsBasis,
        int displayOrder,
        string? caption,
        Guid assertionId)
    {
        if (mediaId == Guid.Empty)
        {
            throw new ArgumentException("Media ID is required.", nameof(mediaId));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(mediaAggregateRevision);

        if (variantId == Guid.Empty)
        {
            throw new ArgumentException("Media variant ID is required.", nameof(variantId));
        }

        ArgumentNullException.ThrowIfNull(objectUri);
        if (!objectUri.IsAbsoluteUri ||
            !objectUri.AbsoluteUri.StartsWith("urn:aggregator:catalog-media:", StringComparison.Ordinal))
        {
            throw new ArgumentException("Media object URI must be an owner-generated Catalog Media URN.", nameof(objectUri));
        }

        var normalizedContentType = contentType?.Trim().ToLowerInvariant();
        if (normalizedContentType is not ("image/jpeg" or "image/png" or "image/webp"))
        {
            throw new ArgumentException("Media content type is unsupported.", nameof(contentType));
        }

        if (!Enum.IsDefined(rightsBasis))
        {
            throw new ArgumentOutOfRangeException(nameof(rightsBasis));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(displayOrder);
        var normalizedCaption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (normalizedCaption is { Length: > 500 } || normalizedCaption?.Any(char.IsControl) == true)
        {
            throw new ArgumentException("Media caption is invalid.", nameof(caption));
        }

        if (assertionId == Guid.Empty)
        {
            throw new ArgumentException("Assertion ID is required for media.", nameof(assertionId));
        }

        return new MediaReference(
            mediaId,
            mediaAggregateRevision,
            variantId,
            objectUri,
            normalizedContentType,
            CatalogDigest.RequireSha256(contentDigest, nameof(contentDigest)),
            rightsBasis,
            displayOrder,
            normalizedCaption,
            assertionId);
    }
}

public sealed class ListingRevisionContent
{
    private ListingRevisionContent(
        IReadOnlyDictionary<LocaleCode, LocalizedTextValue> names,
        IReadOnlyDictionary<LocaleCode, LocalizedTextValue> descriptions,
        IReadOnlySet<CategoryAssignment> categories,
        IReadOnlyDictionary<AttributeKey, ListingAttributeValue> attributes,
        GeographyValue geography,
        IReadOnlyList<ContactValue> contacts,
        IReadOnlyList<MediaReference> media,
        IReadOnlyDictionary<Guid, ProvenanceAssertion> assertions)
    {
        Names = names;
        Descriptions = descriptions;
        Categories = categories;
        Attributes = attributes;
        Geography = geography;
        Contacts = contacts;
        Media = media;
        Assertions = assertions;
    }

    public IReadOnlyDictionary<LocaleCode, LocalizedTextValue> Names { get; }

    public IReadOnlyDictionary<LocaleCode, LocalizedTextValue> Descriptions { get; }

    public IReadOnlySet<CategoryAssignment> Categories { get; }

    public IReadOnlyDictionary<AttributeKey, ListingAttributeValue> Attributes { get; }

    public GeographyValue Geography { get; }

    public IReadOnlyList<ContactValue> Contacts { get; }

    public IReadOnlyList<MediaReference> Media { get; }

    public IReadOnlyDictionary<Guid, ProvenanceAssertion> Assertions { get; }

    public static ListingRevisionContent Create(
        SubjectKind subjectKind,
        IEnumerable<LocalizedTextValue> names,
        IEnumerable<LocalizedTextValue> descriptions,
        IEnumerable<CategoryAssignment> categories,
        IEnumerable<ListingAttributeValue> attributes,
        GeographyValue geography,
        IEnumerable<ContactValue> contacts,
        IEnumerable<MediaReference> media,
        IEnumerable<ProvenanceAssertion> assertions,
        ProductConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(descriptions);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(geography);
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(assertions);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.Catalog.AllowedListingKinds.Contains(subjectKind))
        {
            throw new CatalogInvariantException($"Subject kind '{subjectKind}' cannot be listed in catalog '{configuration.Catalog.Key}'.");
        }

        var assertionMap = assertions.ToDictionary(assertion => assertion.Id);
        var nameMap = names.ToDictionary(value => value.Locale);
        var descriptionMap = descriptions.ToDictionary(value => value.Locale);
        var categorySet = categories.ToHashSet();
        var attributeMap = attributes.ToDictionary(value => value.Key);
        var contactList = contacts
            .OrderBy(contact => contact.Kind)
            .ThenBy(contact => contact.Target.AbsoluteUri, StringComparer.Ordinal)
            .ThenBy(contact => contact.Id)
            .ToArray();
        var mediaList = media
            .OrderBy(item => item.DisplayOrder)
            .ThenBy(item => item.MediaId)
            .ThenBy(item => item.VariantId)
            .ToArray();

        if (nameMap.Count == 0)
        {
            throw new CatalogInvariantException("A listing revision must describe the name state for at least one locale.");
        }

        if (categorySet.Count == 0)
        {
            throw new CatalogInvariantException("A listing revision must have at least one category.");
        }

        foreach (var category in categorySet)
        {
            configuration.RequireCategory(category.CategoryKey, subjectKind);
            RequirePublicAssertion(assertionMap, category.AssertionId, $"category:{category.CategoryKey}");
        }

        var categoryKeys = categorySet.Select(category => category.CategoryKey).ToHashSet();
        foreach (var attribute in attributeMap.Values)
        {
            var definition = configuration.RequireAttribute(attribute.Key, categoryKeys);
            if (attribute.State == FieldValueState.Observed)
            {
                (attribute.Value ?? throw new CatalogInvariantException($"Observed attribute '{attribute.Key}' lacks a value."))
                    .EnsureMatches(definition);
                RequirePublicAssertion(
                    assertionMap,
                    attribute.AssertionId ?? throw new CatalogInvariantException($"Observed attribute '{attribute.Key}' lacks provenance."),
                    $"attribute:{attribute.Key}");
            }
            else if (attribute.Value is not null || attribute.AssertionId is not null)
            {
                throw new CatalogInvariantException($"Non-observed attribute '{attribute.Key}' cannot carry an observed value or assertion.");
            }
        }

        ValidateLocalizedValues(nameMap.Values, configuration, assertionMap, "name");
        ValidateLocalizedValues(descriptionMap.Values, configuration, assertionMap, "description");
        RequirePublicAssertion(assertionMap, geography.AssertionId, "geography");

        foreach (var contact in contactList)
        {
            RequirePublicAssertion(assertionMap, contact.AssertionId, $"contact:{contact.Kind}");
        }

        if (contactList.Select(contact => contact.Id).Distinct().Count() != contactList.Length)
        {
            throw new CatalogInvariantException("A listing revision cannot contain duplicate contact identities.");
        }

        if (contactList
            .GroupBy(contact => new { contact.Kind, Target = contact.Target.AbsoluteUri })
            .Any(group => group.Count() > 1))
        {
            throw new CatalogInvariantException("A listing revision cannot contain duplicate contact targets of the same kind.");
        }

        foreach (var mediaItem in mediaList)
        {
            RequirePublicAssertion(assertionMap, mediaItem.AssertionId, $"media:{mediaItem.MediaId}");
        }

        if (mediaList.Select(item => item.MediaId).Distinct().Count() != mediaList.Length)
        {
            throw new CatalogInvariantException("A listing revision cannot bind one media asset more than once.");
        }

        if (mediaList.Select(item => item.VariantId).Distinct().Count() != mediaList.Length)
        {
            throw new CatalogInvariantException("A listing revision cannot bind one media variant more than once.");
        }

        if (mediaList.Select(item => item.DisplayOrder).Distinct().Count() != mediaList.Length)
        {
            throw new CatalogInvariantException("A listing revision cannot assign the same display order to multiple media bindings.");
        }

        return new ListingRevisionContent(
            new ReadOnlyDictionary<LocaleCode, LocalizedTextValue>(nameMap),
            new ReadOnlyDictionary<LocaleCode, LocalizedTextValue>(descriptionMap),
            new ReadOnlySet<CategoryAssignment>(categorySet),
            new ReadOnlyDictionary<AttributeKey, ListingAttributeValue>(attributeMap),
            geography,
            Array.AsReadOnly(contactList),
            Array.AsReadOnly(mediaList),
            new ReadOnlyDictionary<Guid, ProvenanceAssertion>(assertionMap));
    }

    public void EnsurePublishable(ProductConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!Names.TryGetValue(configuration.Site.DefaultLocale, out var defaultName) ||
            defaultName.State != FieldValueState.Observed)
        {
            throw new CatalogInvariantException(
                $"A publishable listing requires an observed name in default locale '{configuration.Site.DefaultLocale}'.");
        }

        foreach (var definition in configuration.Attributes.Values.Where(
                     attribute => attribute.Requirement == PublicFieldRequirement.RequiredForPublication &&
                                  attribute.Categories.Overlaps(Categories.Select(category => category.CategoryKey).ToHashSet())))
        {
            if (!Attributes.TryGetValue(definition.Key, out var value) || value.State != FieldValueState.Observed)
            {
                throw new CatalogInvariantException($"Required attribute '{definition.Key}' is not observed.");
            }
        }

        if (Geography.State == GeographyState.Unresolved)
        {
            throw new CatalogInvariantException("A listing with unresolved geography cannot be published.");
        }
    }

    private static void ValidateLocalizedValues(
        IEnumerable<LocalizedTextValue> values,
        ProductConfiguration configuration,
        IReadOnlyDictionary<Guid, ProvenanceAssertion> assertions,
        string fieldName)
    {
        foreach (var value in values)
        {
            if (!configuration.Site.SupportedLocales.Contains(value.Locale))
            {
                throw new CatalogInvariantException($"Locale '{value.Locale}' is not supported by site '{configuration.Site.Key}'.");
            }

            if (value.State == FieldValueState.Observed)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(value.Value);
                RequirePublicAssertion(
                    assertions,
                    value.AssertionId ?? throw new CatalogInvariantException($"Observed {fieldName} lacks provenance."),
                    $"{fieldName}:{value.Locale}");
            }
            else if (value.Value is not null || value.AssertionId is not null || value.MissingReason is null)
            {
                throw new CatalogInvariantException(
                    $"Non-observed {fieldName} for locale '{value.Locale}' must carry only an explicit missing reason.");
            }
        }
    }

    private static void RequirePublicAssertion(
        IReadOnlyDictionary<Guid, ProvenanceAssertion> assertions,
        Guid assertionId,
        string fieldKey)
    {
        if (!assertions.TryGetValue(assertionId, out var assertion))
        {
            throw new CatalogInvariantException($"Field '{fieldKey}' references unknown assertion '{assertionId}'.");
        }

        assertion.EnsurePublicUseAllowed(fieldKey);
    }
}
