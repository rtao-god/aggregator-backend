namespace Aggregator.Catalog.Contracts;

public enum PublicationRequestStateContract
{
    Pending = 1,
    Processing = 2,
    Sealed = 3,
    Failed = 4,
    Cancelled = 5,
}

public sealed record SelectedListingRevisionDto(Guid ListingId, Guid ListingRevisionId);

public sealed record CreatePublicationRequest(
    Guid CatalogId,
    Guid? ExpectedCurrentPublicationId,
    Guid ProductConfigurationRevisionId,
    Guid TaxonomyRevisionId,
    Guid AttributeRevisionId,
    Guid MarketAreaRevisionId,
    IReadOnlyList<SelectedListingRevisionDto> SelectedListings,
    string Reason);

public sealed record PublicationRequestDto(
    Guid PublicationRequestId,
    Guid PublicationId,
    Guid CatalogId,
    Guid? ExpectedCurrentPublicationId,
    Guid ProductConfigurationRevisionId,
    Guid TaxonomyRevisionId,
    Guid AttributeRevisionId,
    Guid MarketAreaRevisionId,
    IReadOnlyList<SelectedListingRevisionDto> SelectedListings,
    string Reason,
    Guid RequestedBy,
    DateTimeOffset RequestedAtUtc,
    PublicationRequestStateContract State,
    long AggregateRevision,
    string? FailureCode);

public sealed record CatalogPublicationListingDto(
    Guid ListingId,
    Guid ListingRevisionId,
    ListingKindContract ListingKind,
    Guid SubjectId,
    IReadOnlyList<LocalizedListingContentDto> Translations,
    IReadOnlyList<string> CategoryKeys,
    IReadOnlyList<ListingAttributeValueDto> Attributes,
    string ContentDigest);

public sealed record CatalogPublicationBundleDto(
    string SchemaIdentity,
    Guid PublicationId,
    Guid CatalogId,
    Guid? PreviousPublicationId,
    Guid ProductConfigurationRevisionId,
    Guid TaxonomyRevisionId,
    Guid AttributeRevisionId,
    Guid MarketAreaRevisionId,
    DateTimeOffset GeneratedAtUtc,
    string GeneratorBuild,
    int ListingCount,
    string ListingIndexDigest,
    string RouteManifestDigest,
    IReadOnlyList<CatalogPublicationListingDto> Listings,
    IReadOnlyDictionary<string, string> RouteManifest,
    IReadOnlyDictionary<string, string> RedirectManifest,
    IReadOnlyList<string> MediaManifest);

public sealed record PublicationArtifactDto(
    string ObjectKey,
    string ContentDigest,
    long Size,
    string SchemaIdentity);

public sealed record CatalogPublicationDto(
    Guid PublicationId,
    Guid CatalogId,
    Guid? PreviousPublicationId,
    Guid ProductConfigurationRevisionId,
    Guid TaxonomyRevisionId,
    Guid AttributeRevisionId,
    Guid MarketAreaRevisionId,
    PublicationArtifactDto Artifact,
    int ListingCount,
    DateTimeOffset SealedAtUtc,
    Guid ActivatedBy);

public sealed record CatalogPublicationActivated(
    Guid MessageId,
    string ContractIdentity,
    Guid PublicationId,
    Guid CatalogId,
    Guid? PreviousPublicationId,
    string ArtifactKey,
    string ArtifactDigest,
    string SchemaIdentity,
    int ListingCount,
    long AggregateRevision,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId);
