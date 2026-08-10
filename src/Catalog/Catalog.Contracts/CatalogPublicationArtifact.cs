namespace Aggregator.Catalog.Contracts;

public static class CatalogPublicationArtifactContract
{
    public const string Identity = "aggregator-catalog-publication";
    public const int Revision = 4;
}

public enum CatalogPublicRouteKindContract
{
    Listing = 1,
    Category = 2,
    EditorialLanding = 3,
}

/// <summary>One current authored route sealed into an immutable Catalog publication.</summary>
public sealed record PublicRouteDocument(
    CatalogPublicRouteKindContract RouteKind,
    string RouteGroupKey,
    string Locale,
    string Path,
    DateTimeOffset LastModifiedAtUtc,
    bool IsDraft,
    bool IsSuppressed);

/// <summary>One permanent route transition retained by the Catalog publication owner.</summary>
public sealed record PublicRouteRedirect(
    CatalogPublicRouteKindContract RouteKind,
    string RouteGroupKey,
    string Locale,
    string SourcePath,
    string TargetPath,
    Guid SourcePublicationId,
    string Reason,
    DateTimeOffset CreatedAtUtc);

public sealed record CatalogPublicationArtifact(
    string ContractIdentity,
    int ContractRevision,
    Guid PublicationId,
    string CatalogKey,
    string DefaultLocale,
    IReadOnlyList<string> SupportedLocales,
    Guid ConfigurationRevisionId,
    long PublicationSequence,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<PublicListingDocument> Listings)
{
    private IReadOnlyList<PublicRouteDocument>? routes;
    private IReadOnlyList<PublicRouteRedirect>? redirects;

    /// <summary>
    /// Exact current route manifest. The deterministic listing-ID fallback keeps older sealed
    /// artifacts readable while current producers serialize this property explicitly.
    /// </summary>
    public IReadOnlyList<PublicRouteDocument> Routes
    {
        get => routes ??= CatalogPublicationRouteManifest.BuildListingRoutes(
            Listings,
            CreatedAtUtc);
        init => routes = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Complete redirect history required by the active route set.</summary>
    public IReadOnlyList<PublicRouteRedirect> Redirects
    {
        get => redirects ??= Array.Empty<PublicRouteRedirect>();
        init => redirects = value ?? throw new ArgumentNullException(nameof(value));
    }
}

public static class CatalogPublicationRouteManifest
{
    public static string ListingGroupKey(Guid listingId)
    {
        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        return $"listing:{listingId:D}";
    }

    public static string ListingPath(string locale, Guid listingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        return $"/{locale}/listings/{listingId:N}";
    }

    public static IReadOnlyList<PublicRouteDocument> BuildListingRoutes(
        IReadOnlyList<PublicListingDocument> listings,
        DateTimeOffset lastModifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(listings);
        return listings
            .OrderBy(listing => listing.ListingId)
            .SelectMany(listing => listing.Names
                .Where(name => name.State == FieldValueStateContract.Observed)
                .OrderBy(name => name.Locale, StringComparer.Ordinal)
                .Select(name => new PublicRouteDocument(
                    CatalogPublicRouteKindContract.Listing,
                    ListingGroupKey(listing.ListingId),
                    name.Locale,
                    ListingPath(name.Locale, listing.ListingId),
                    lastModifiedAtUtc,
                    IsDraft: false,
                    IsSuppressed: false)))
            .ToArray();
    }
}

public sealed record PublicListingDocument(
    Guid ListingId,
    Guid ListingRevisionId,
    Guid SubjectId,
    Guid SubjectRevisionId,
    SubjectKindContract SubjectKind,
    IReadOnlyList<PublicLocalizedText> Names,
    IReadOnlyList<PublicLocalizedText> Descriptions,
    IReadOnlyList<string> CategoryKeys,
    IReadOnlyList<PublicAttributeValue> Attributes,
    PublicGeography Geography,
    IReadOnlyList<PublicContact> Contacts,
    IReadOnlyList<PublicMedia> Media,
    IReadOnlyList<PublicProvenanceSummary> Provenance,
    string ContentDigest);

public sealed record PublicLocalizedText(
    string Locale,
    FieldValueStateContract State,
    string? Value,
    MissingValueReasonContract? MissingReason,
    Guid? AssertionId);

public sealed record PublicAttributeValue(
    string AttributeKey,
    FieldValueStateContract State,
    TypedValueContract? Value,
    MissingValueReasonContract? MissingReason,
    Guid? AssertionId);

public sealed record PublicGeography(
    GeographyStateContract State,
    decimal? Latitude,
    decimal? Longitude,
    string? DistrictKey,
    Guid AssertionId);

public sealed record PublicContact(
    Guid ContactId,
    ContactKindContract Kind,
    string Target,
    string? Label,
    Guid AssertionId);

/// <summary>Exact Catalog Media owner output sealed into one publication listing.</summary>
public sealed record PublicMedia(
    Guid MediaId,
    long MediaAggregateRevision,
    Guid VariantId,
    string ObjectUri,
    string ContentType,
    string ContentDigest,
    MediaRightsBasisContract RightsBasis,
    int DisplayOrder,
    string? Caption,
    Guid AssertionId);

public sealed record PublicProvenanceSummary(
    Guid AssertionId,
    SourceKindContract SourceKind,
    DateTimeOffset ObservedAtUtc,
    string EvidenceDigest);
