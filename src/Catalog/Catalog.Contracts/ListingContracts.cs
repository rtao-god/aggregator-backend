using System.Text.Json;

namespace Aggregator.Catalog.Contracts;

public enum ListingLifecycleStateContract
{
    Created = 1,
    Draft = 2,
    ReviewRequired = 3,
    Approved = 4,
    PublicationRequested = 5,
    Published = 6,
    Stale = 7,
    Archived = 8,
    Rejected = 9,
    Disputed = 10,
    Blocked = 11,
}

public enum AttributeValueStateContract
{
    Observed = 1,
    OwnerConfirmed = 2,
    EditorConfirmed = 3,
    Unknown = 4,
    NotDisclosed = 5,
    NotApplicable = 6,
    Disputed = 7,
    Expired = 8,
}

public enum ProvenanceUsagePolicyContract
{
    CommercialAllowed = 1,
    ReferenceOnly = 2,
    ResearchOnly = 3,
    Forbidden = 4,
    Unknown = 5,
}

public sealed record CreateListingRequest(
    Guid CatalogId,
    ListingKindContract ListingKind,
    Guid SubjectId);

public sealed record LocalizedListingContentDto(
    string Locale,
    string Title,
    string Summary);

public sealed record ListingAttributeValueDto(
    string AttributeKey,
    AttributeDataTypeContract DataType,
    AttributeValueStateContract State,
    JsonElement? Value);

public sealed record ProvenanceReferenceDto(
    string FieldPath,
    string SourceKind,
    string SourceReference,
    ProvenanceUsagePolicyContract UsagePolicy,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ValidUntilUtc);

public sealed record CreateListingRevisionRequest(
    Guid SubjectRevisionId,
    Guid ProductConfigurationRevisionId,
    Guid TaxonomyRevisionId,
    Guid AttributeRevisionId,
    Guid MarketAreaRevisionId,
    IReadOnlyList<LocalizedListingContentDto> Translations,
    IReadOnlyList<string> CategoryKeys,
    IReadOnlyList<ListingAttributeValueDto> Attributes,
    IReadOnlyList<ProvenanceReferenceDto> Provenance);

public sealed record ListingLifecycleCommandRequest(string Reason);

public sealed record ListingDto(
    Guid ListingId,
    Guid CatalogId,
    ListingKindContract ListingKind,
    Guid SubjectId,
    ListingLifecycleStateContract State,
    Guid? CurrentDraftRevisionId,
    Guid? CurrentPublishedRevisionId,
    Guid? CurrentPublicationId,
    long AggregateRevision,
    string? ArchiveReason,
    Guid LastChangedBy,
    DateTimeOffset LastChangedAtUtc);

public sealed record ListingRevisionDto(
    Guid ListingRevisionId,
    Guid ListingId,
    Guid SubjectRevisionId,
    Guid ProductConfigurationRevisionId,
    Guid TaxonomyRevisionId,
    Guid AttributeRevisionId,
    Guid MarketAreaRevisionId,
    IReadOnlyList<LocalizedListingContentDto> Translations,
    IReadOnlyList<string> CategoryKeys,
    IReadOnlyList<ListingAttributeValueDto> Attributes,
    IReadOnlyList<ProvenanceReferenceDto> Provenance,
    string ContentDigest,
    Guid CreatedBy,
    DateTimeOffset CreatedAtUtc);

public sealed record ListingRevisionCreatedDto(ListingDto Listing, ListingRevisionDto Revision);
