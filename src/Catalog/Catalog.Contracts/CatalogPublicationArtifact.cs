namespace Aggregator.Catalog.Contracts;

public static class CatalogPublicationArtifactContract
{
    public const string Identity = "aggregator-catalog-publication";
    public const int Revision = 2;
}

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
    IReadOnlyList<PublicListingDocument> Listings);

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
    ContactKindContract Kind,
    string Target,
    string? Label,
    Guid AssertionId);

public sealed record PublicMedia(
    Guid MediaId,
    string ObjectUri,
    string ContentType,
    string ContentDigest,
    MediaRightsBasisContract RightsBasis,
    Guid AssertionId);

public sealed record PublicProvenanceSummary(
    Guid AssertionId,
    SourceKindContract SourceKind,
    DateTimeOffset ObservedAtUtc,
    string EvidenceDigest);
