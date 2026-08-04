namespace Aggregator.Catalog.Contracts;

public static class CatalogPublicationArtifactContract
{
    public const string Identity = "aggregator-catalog-publication";
    public const int Revision = 1;
}

public sealed record CatalogPublicationArtifactV1(
    string ContractIdentity,
    int ContractRevision,
    Guid PublicationId,
    string CatalogKey,
    Guid ConfigurationRevisionId,
    long PublicationSequence,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<PublicListingDocumentV1> Listings);

public sealed record PublicListingDocumentV1(
    Guid ListingId,
    Guid ListingRevisionId,
    Guid SubjectId,
    Guid SubjectRevisionId,
    SubjectKindContract SubjectKind,
    IReadOnlyList<PublicLocalizedTextV1> Names,
    IReadOnlyList<PublicLocalizedTextV1> Descriptions,
    IReadOnlyList<string> CategoryKeys,
    IReadOnlyList<PublicAttributeV1> Attributes,
    PublicGeographyV1 Geography,
    IReadOnlyList<PublicContactV1> Contacts,
    IReadOnlyList<PublicMediaV1> Media,
    IReadOnlyList<PublicProvenanceSummaryV1> Provenance,
    string ContentDigest);

public sealed record PublicLocalizedTextV1(
    string Locale,
    FieldValueStateContract State,
    string? Value,
    MissingValueReasonContract? MissingReason,
    Guid? AssertionId);

public sealed record PublicAttributeV1(
    string AttributeKey,
    FieldValueStateContract State,
    TypedValueContract? Value,
    MissingValueReasonContract? MissingReason,
    Guid? AssertionId);

public sealed record PublicGeographyV1(
    GeographyStateContract State,
    decimal? Latitude,
    decimal? Longitude,
    string? DistrictKey,
    Guid AssertionId);

public sealed record PublicContactV1(
    ContactKindContract Kind,
    string Target,
    string? Label,
    Guid AssertionId);

public sealed record PublicMediaV1(
    Guid MediaId,
    string ObjectUri,
    string ContentType,
    string ContentDigest,
    MediaRightsBasisContract RightsBasis,
    Guid AssertionId);

public sealed record PublicProvenanceSummaryV1(
    Guid AssertionId,
    SourceKindContract SourceKind,
    DateTimeOffset ObservedAtUtc,
    string EvidenceDigest);
