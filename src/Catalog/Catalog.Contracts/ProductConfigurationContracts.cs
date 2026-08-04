namespace Aggregator.Catalog.Contracts;

public enum ListingKindContract
{
    Place = 1,
    Provider = 2,
}

public enum AttributeDataTypeContract
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

public sealed record SiteConfigurationDto(
    string Key,
    string DefaultLocale,
    IReadOnlyList<string> SupportedLocales,
    string DefaultCurrency,
    string TimeZone,
    string BrandKey,
    IReadOnlyList<string> HostMappings,
    IReadOnlyDictionary<string, string> LegalPageReferences);

public sealed record CatalogConfigurationDto(
    Guid CatalogId,
    string Key,
    string SiteKey,
    IReadOnlyDictionary<string, string> Titles,
    string MarketAreaKey,
    Guid TaxonomyRevisionId,
    Guid AttributeRevisionId,
    Guid MarketAreaRevisionId,
    string Currency,
    string TimeZone,
    IReadOnlyList<ListingKindContract> SupportedListingKinds,
    string SeoPolicyKey,
    string PublicationPolicyKey,
    string ContactPolicyKey,
    string ClaimPolicyKey,
    string PromotionEligibilityPolicyKey);

public sealed record CategoryDefinitionDto(
    string Key,
    string? ParentKey,
    IReadOnlyDictionary<string, string> Names,
    IReadOnlyDictionary<string, string> Slugs,
    IReadOnlyList<ListingKindContract> AllowedListingKinds,
    bool PrimaryAllowed,
    bool SeoIndexable,
    int SortOrder);

public sealed record AttributeDefinitionDto(
    string Key,
    AttributeDataTypeContract DataType,
    bool Multiple,
    bool Filterable,
    bool Comparable,
    bool Sortable,
    bool Public,
    IReadOnlyList<string> AllowedOptions,
    IReadOnlyDictionary<string, string> Labels);

public sealed record CategoryAttributeDefinitionDto(
    string CategoryKey,
    string AttributeKey,
    bool RequiredForDraft,
    bool RequiredForPublication,
    bool FilterableInCategory,
    bool Comparable,
    bool VisibleInCard,
    IReadOnlyList<ListingKindContract> AllowedListingKinds,
    string DisplayGroup,
    int DisplayOrder);

public sealed record ImportProductConfigurationRequest(
    string SemanticIdentity,
    string SourceCommitIdentity,
    SiteConfigurationDto Site,
    CatalogConfigurationDto Catalog,
    IReadOnlyList<CategoryDefinitionDto> Categories,
    IReadOnlyList<AttributeDefinitionDto> Attributes,
    IReadOnlyList<CategoryAttributeDefinitionDto> CategoryAttributes);

public sealed record ProductConfigurationRevisionDto(
    Guid RevisionId,
    Guid CatalogId,
    string SemanticIdentity,
    string ContentDigest,
    string SourceCommitIdentity,
    Guid CreatedBy,
    DateTimeOffset CreatedAtUtc,
    bool Active);

public sealed record ActivateProductConfigurationRequest(
    Guid? ExpectedActiveRevisionId,
    string Reason);

public sealed record ProductConfigurationActivationDto(
    Guid CatalogId,
    Guid RevisionId,
    Guid? PreviousRevisionId,
    Guid ActivatedBy,
    DateTimeOffset ActivatedAtUtc,
    string Reason);
