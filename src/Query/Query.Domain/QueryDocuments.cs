namespace Aggregator.Query.Domain;

public enum QueryListingKind
{
    Place = 1,
    Provider = 2,
}

public enum QueryFieldState
{
    Observed = 1,
    Missing = 2,
    NotApplicable = 3,
    Withheld = 4,
}

public enum QueryValueKind
{
    BooleanValue = 1,
    DecimalNumber = 2,
    TextValue = 3,
    TextCollection = 4,
    DurationMinutes = 5,
}

public enum QueryGeographyState
{
    PrimaryMarket = 1,
    NearbyMarket = 2,
    RemoteOnly = 3,
    OutsideMarket = 4,
}

public enum QueryContactKind
{
    Website = 1,
    Email = 2,
    Phone = 3,
    WhatsApp = 4,
    BookingReference = 5,
    MapReference = 6,
}

public enum QueryMediaRightsBasis
{
    OwnerProvided = 1,
    ExplicitLicense = 2,
    OriginalEditorialWork = 3,
    PublicDomain = 4,
}

public sealed record QueryLocalizedDocument(
    string Locale,
    string RoutePath,
    string Title,
    QueryFieldState DescriptionState,
    string? Description);

public sealed record QueryAttributeDocument(
    string AttributeKey,
    QueryFieldState State,
    QueryValueKind? ValueKind,
    bool? BooleanValue,
    decimal? DecimalValue,
    string? TextValue,
    IReadOnlyList<string>? TextCollectionValue);

public sealed record QueryGeographyDocument(
    QueryGeographyState State,
    decimal? Latitude,
    decimal? Longitude,
    string? DistrictKey);

public sealed record QueryContactDocument(
    Guid ContactId,
    QueryContactKind Kind,
    string Target,
    string? Label);

public sealed record QueryMediaDocument(
    Guid MediaId,
    string ObjectUri,
    string ContentType,
    string ContentDigest,
    QueryMediaRightsBasis RightsBasis);

public sealed class QueryListingDocument
{
    private QueryListingDocument(
        Guid listingId,
        Guid listingRevisionId,
        Guid subjectId,
        Guid subjectRevisionId,
        QueryListingKind listingKind,
        IReadOnlyList<QueryLocalizedDocument> localizations,
        IReadOnlyList<string> categoryKeys,
        IReadOnlyList<QueryAttributeDocument> attributes,
        QueryGeographyDocument geography,
        IReadOnlyList<QueryContactDocument> contacts,
        IReadOnlyList<QueryMediaDocument> media,
        string sourceContentDigest,
        DateTimeOffset publishedAtUtc)
    {
        ListingId = listingId;
        ListingRevisionId = listingRevisionId;
        SubjectId = subjectId;
        SubjectRevisionId = subjectRevisionId;
        ListingKind = listingKind;
        Localizations = localizations;
        CategoryKeys = categoryKeys;
        Attributes = attributes;
        Geography = geography;
        Contacts = contacts;
        Media = media;
        SourceContentDigest = sourceContentDigest;
        PublishedAtUtc = publishedAtUtc;
    }

    public Guid ListingId { get; }

    public Guid ListingRevisionId { get; }

    public Guid SubjectId { get; }

    public Guid SubjectRevisionId { get; }

    public QueryListingKind ListingKind { get; }

    public IReadOnlyList<QueryLocalizedDocument> Localizations { get; }

    public IReadOnlyList<string> CategoryKeys { get; }

    public IReadOnlyList<QueryAttributeDocument> Attributes { get; }

    public QueryGeographyDocument Geography { get; }

    public IReadOnlyList<QueryContactDocument> Contacts { get; }

    public IReadOnlyList<QueryMediaDocument> Media { get; }

    public string SourceContentDigest { get; }

    public DateTimeOffset PublishedAtUtc { get; }

    public static QueryListingDocument Create(
        Guid listingId,
        Guid listingRevisionId,
        Guid subjectId,
        Guid subjectRevisionId,
        QueryListingKind listingKind,
        IEnumerable<QueryLocalizedDocument> localizations,
        IEnumerable<string> categoryKeys,
        IEnumerable<QueryAttributeDocument> attributes,
        QueryGeographyDocument geography,
        IEnumerable<QueryContactDocument> contacts,
        IEnumerable<QueryMediaDocument> media,
        string sourceContentDigest,
        DateTimeOffset publishedAtUtc)
    {
        QueryContractRules.RequireId(listingId, nameof(listingId));
        QueryContractRules.RequireId(listingRevisionId, nameof(listingRevisionId));
        QueryContractRules.RequireId(subjectId, nameof(subjectId));
        QueryContractRules.RequireId(subjectRevisionId, nameof(subjectRevisionId));
        ArgumentNullException.ThrowIfNull(localizations);
        ArgumentNullException.ThrowIfNull(categoryKeys);
        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentNullException.ThrowIfNull(geography);
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(media);

        var localizationArray = localizations
            .OrderBy(item => item.Locale, StringComparer.Ordinal)
            .ToArray();
        if (localizationArray.Length == 0)
        {
            throw new QueryDomainException("QUERY_LOCALIZATION_REQUIRED", "A public listing document requires at least one observed localized title.");
        }

        var localeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var localization in localizationArray)
        {
            QueryContractRules.RequireText(localization.Locale, nameof(localizations), 35);
            QueryContractRules.RequireText(localization.RoutePath, nameof(localizations), 500);
            QueryContractRules.RequireText(localization.Title, nameof(localizations), 300);
            if (!localization.RoutePath.StartsWith('/'))
            {
                throw new QueryDomainException("QUERY_ROUTE_PATH_INVALID", "A public route path must be absolute within the site.");
            }

            if (!localeSet.Add(localization.Locale))
            {
                throw new QueryDomainException("QUERY_LOCALE_DUPLICATE", $"Locale '{localization.Locale}' is duplicated for listing '{listingId}'.");
            }

            if (localization.DescriptionState == QueryFieldState.Observed && string.IsNullOrWhiteSpace(localization.Description))
            {
                throw new QueryDomainException("QUERY_DESCRIPTION_REQUIRED", "An observed description state requires text.");
            }

            if (localization.DescriptionState != QueryFieldState.Observed && localization.Description is not null)
            {
                throw new QueryDomainException("QUERY_DESCRIPTION_STATE_INVALID", "A non-observed description state cannot carry text.");
            }
        }

        var categories = categoryKeys
            .Select(item => QueryContractRules.RequireKey(item, nameof(categoryKeys)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (categories.Length == 0)
        {
            throw new QueryDomainException("QUERY_CATEGORY_REQUIRED", "A public listing document requires at least one category.");
        }

        var attributeArray = attributes
            .OrderBy(item => item.AttributeKey, StringComparer.Ordinal)
            .ToArray();
        if (attributeArray.Select(item => item.AttributeKey).Distinct(StringComparer.Ordinal).Count() != attributeArray.Length)
        {
            throw new QueryDomainException("QUERY_ATTRIBUTE_DUPLICATE", $"Listing '{listingId}' contains a duplicated projected attribute.");
        }

        var contactArray = contacts
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Target, StringComparer.Ordinal)
            .ThenBy(item => item.ContactId)
            .ToArray();
        foreach (var contact in contactArray)
        {
            QueryContractRules.RequireId(contact.ContactId, nameof(contacts));
            QueryContractRules.RequireText(contact.Target, nameof(contacts), 2048);
        }

        if (contactArray.Select(item => item.ContactId).Distinct().Count() != contactArray.Length)
        {
            throw new QueryDomainException(
                "QUERY_CONTACT_IDENTITY_DUPLICATE",
                $"Listing '{listingId}' contains a duplicated projected contact identity.");
        }

        if (contactArray
            .GroupBy(item => new { item.Kind, item.Target })
            .Any(group => group.Count() > 1))
        {
            throw new QueryDomainException(
                "QUERY_CONTACT_TARGET_DUPLICATE",
                $"Listing '{listingId}' contains a duplicated projected contact target.");
        }

        return new QueryListingDocument(
            listingId,
            listingRevisionId,
            subjectId,
            subjectRevisionId,
            listingKind,
            Array.AsReadOnly(localizationArray),
            Array.AsReadOnly(categories),
            Array.AsReadOnly(attributeArray),
            geography,
            Array.AsReadOnly(contactArray),
            Array.AsReadOnly(media.ToArray()),
            QueryContractRules.RequireDigest(sourceContentDigest, nameof(sourceContentDigest)),
            QueryContractRules.RequireUtc(publishedAtUtc, nameof(publishedAtUtc)));
    }
}
