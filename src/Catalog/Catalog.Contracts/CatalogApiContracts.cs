namespace Aggregator.Catalog.Contracts;

public static class CatalogContractIdentity
{
    public const string ProductConfiguration = "aggregator-catalog-product-configuration";
    public const int ProductConfigurationRevision = 1;
    public const string AdminApi = "aggregator-catalog-admin";
    public const int AdminApiRevision = 1;
}

public enum SubjectKindContract
{
    Organization = 1,
    Place = 2,
    Provider = 3,
}

public enum AttributeValueKindContract
{
    Boolean = 1,
    Decimal = 2,
    Text = 3,
    TextSet = 4,
    DurationMinutes = 5,
}

public enum AttributeCardinalityContract
{
    Single = 1,
    Multiple = 2,
}

public enum PublicFieldRequirementContract
{
    Optional = 1,
    RequiredForPublication = 2,
}

public enum SourceKindContract
{
    FirstPartySubmission = 1,
    PublicWebsite = 2,
    PublicDirectoryReference = 3,
    EditorialResearch = 4,
    OwnerVerification = 5,
    LicensedDataset = 6,
}

public enum UsagePolicyContract
{
    PublicAllowed = 1,
    ReferenceOnly = 2,
    ResearchOnly = 3,
    Forbidden = 4,
}

public enum FieldValueStateContract
{
    Observed = 1,
    Missing = 2,
    NotApplicable = 3,
    Withheld = 4,
}

public enum MissingValueReasonContract
{
    NotPublishedBySource = 1,
    NotCollected = 2,
    ConflictingEvidence = 3,
    RightsRestricted = 4,
    OwnerWithheld = 5,
}

public enum ContactKindContract
{
    Website = 1,
    Email = 2,
    Phone = 3,
    WhatsApp = 4,
    BookingReference = 5,
    MapReference = 6,
}

public enum GeographyStateContract
{
    BerlinCore = 1,
    BerlinNearby = 2,
    RemoteOnly = 3,
    OutsideMarket = 4,
    Unresolved = 5,
}

public enum MediaRightsBasisContract
{
    OwnerProvided = 1,
    ExplicitLicense = 2,
    OriginalEditorialWork = 3,
    PublicDomain = 4,
}

public enum ListingLifecycleStateContract
{
    Draft = 1,
    Approved = 2,
    Published = 3,
    Archived = 4,
}

public enum ClaimStateContract
{
    Pending = 1,
    Verified = 2,
    Rejected = 3,
    Revoked = 4,
}

public enum ListingAccessScopeContract
{
    ReadDraft = 1,
    ProposeRevision = 2,
    ManageContacts = 3,
    ManageMedia = 4,
}

public sealed record ImportProductConfigurationRequest(
    string ContractIdentity,
    int ContractRevision,
    string ExpectedContentDigest,
    ProductConfigurationContract Configuration);

public sealed record ProductConfigurationContract(
    Guid RevisionId,
    DateTimeOffset CreatedAtUtc,
    SiteDefinitionContract Site,
    CatalogDefinitionContract Catalog,
    IReadOnlyList<CategoryDefinitionContract> Categories,
    IReadOnlyList<AttributeDefinitionContract> Attributes);

public sealed record SiteDefinitionContract(
    string Key,
    string DefaultLocale,
    IReadOnlyList<string> SupportedLocales,
    string Currency,
    string TimeZone);

public sealed record CatalogDefinitionContract(
    string Key,
    string SiteKey,
    string MarketAreaKey,
    string Currency,
    string TimeZone,
    IReadOnlyList<SubjectKindContract> AllowedListingKinds);

public sealed record CategoryDefinitionContract(
    string Key,
    IReadOnlyList<SubjectKindContract> SubjectKinds,
    IReadOnlyDictionary<string, string> LocalizedNames,
    bool IsActive);

public sealed record AttributeDefinitionContract(
    string Key,
    AttributeValueKindContract ValueKind,
    AttributeCardinalityContract Cardinality,
    PublicFieldRequirementContract Requirement,
    IReadOnlyList<string> Categories,
    IReadOnlyDictionary<string, string> LocalizedNames,
    decimal? Minimum,
    decimal? Maximum,
    IReadOnlyList<string> AllowedValues,
    bool IsFilterable,
    bool IsSortable);

public sealed record ProductConfigurationRevisionResponse(
    Guid RevisionId,
    string SiteKey,
    string CatalogKey,
    string ContentDigest,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ImportedAtUtc,
    bool IsActive);

public sealed record SubjectReferenceContract(
    Guid SubjectId,
    Guid SubjectRevisionId,
    SubjectKindContract Kind);

public sealed record CreateListingRequest(
    string CatalogKey,
    SubjectReferenceContract Subject);

public sealed record CreateListingRevisionRequest(
    long ExpectedVersion,
    Guid ConfigurationRevisionId,
    SubjectReferenceContract Subject,
    ListingRevisionContentContract Content);

public sealed record ListingRevisionContentContract(
    IReadOnlyList<LocalizedTextValueContract> Names,
    IReadOnlyList<LocalizedTextValueContract> Descriptions,
    IReadOnlyList<CategoryAssignmentContract> Categories,
    IReadOnlyList<ListingAttributeValueContract> Attributes,
    GeographyValueContract Geography,
    IReadOnlyList<ContactValueContract> Contacts,
    IReadOnlyList<MediaReferenceContract> Media,
    IReadOnlyList<ProvenanceAssertionContract> Assertions);

public sealed record LocalizedTextValueContract(
    string Locale,
    FieldValueStateContract State,
    string? Value,
    Guid? AssertionId,
    MissingValueReasonContract? MissingReason);

public sealed record CategoryAssignmentContract(
    string CategoryKey,
    Guid AssertionId);

public sealed record ListingAttributeValueContract(
    string AttributeKey,
    FieldValueStateContract State,
    TypedValueContract? Value,
    Guid? AssertionId,
    MissingValueReasonContract? MissingReason);

public sealed record TypedValueContract(
    AttributeValueKindContract Kind,
    bool? BooleanValue,
    decimal? DecimalValue,
    string? TextValue,
    IReadOnlyList<string>? TextSetValue);

public sealed record GeographyValueContract(
    GeographyStateContract State,
    decimal? Latitude,
    decimal? Longitude,
    string? DistrictKey,
    Guid AssertionId);

public sealed record ContactValueContract(
    ContactKindContract Kind,
    string Target,
    string? Label,
    Guid AssertionId);

/// <summary>
/// References one exact Catalog Media asset revision and public variant without accepting media-owned metadata from the caller.
/// </summary>
public sealed record MediaReferenceContract(
    Guid MediaId,
    long ExpectedMediaAggregateRevision,
    Guid VariantId,
    int DisplayOrder,
    string? Caption,
    Guid AssertionId);

public sealed record ProvenanceAssertionContract(
    Guid Id,
    SourceKindContract SourceKind,
    string SourceReference,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset RecordedAtUtc,
    UsagePolicyContract UsagePolicy,
    string EvidenceDigest);

public sealed record ListingResponse(
    Guid Id,
    string CatalogKey,
    SubjectReferenceContract Subject,
    ListingLifecycleStateContract State,
    long Version,
    long LatestRevisionNumber,
    Guid? CurrentDraftRevisionId,
    Guid? ApprovedRevisionId,
    Guid? PublishedRevisionId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ListingRevisionResponse(
    Guid Id,
    Guid ListingId,
    long RevisionNumber,
    Guid ConfigurationRevisionId,
    SubjectReferenceContract Subject,
    string ContentDigest,
    DateTimeOffset CreatedAtUtc);

public sealed record ApproveListingRevisionRequest(
    long ExpectedVersion,
    Guid RevisionId);

public sealed record RejectListingRevisionRequest(
    long ExpectedVersion,
    Guid RevisionId,
    string Reason);

public sealed record ArchiveListingRequest(
    long ExpectedVersion);

public sealed record EditorialDecisionResponse(
    Guid Id,
    Guid ListingId,
    Guid RevisionId,
    string Decision,
    string? Reason,
    DateTimeOffset DecidedAtUtc);

public sealed record PublicationSelectionContract(
    Guid ListingId,
    Guid ListingRevisionId,
    long ExpectedListingVersion);

public sealed record RollbackPublicationRequest(
    Guid TargetPublicationId,
    Guid ExpectedCurrentPublicationId);

public sealed record PublicationEntryContract(
    Guid ListingId,
    Guid ListingRevisionId,
    Guid SubjectRevisionId,
    string ContentDigest);

public sealed record CatalogPublicationResponse(
    Guid Id,
    string CatalogKey,
    Guid ConfigurationRevisionId,
    long Sequence,
    string ArtifactKey,
    string ArtifactDigest,
    IReadOnlyList<PublicationEntryContract> Entries,
    DateTimeOffset CreatedAtUtc,
    bool IsCurrent);

public sealed record SubmitListingClaimRequest(
    Guid ListingId,
    string EvidenceReference,
    string EvidenceDigest);

public sealed record VerifyListingClaimRequest(
    IReadOnlyList<ListingAccessScopeContract> Scopes,
    DateTimeOffset? ExpiresAtUtc);

public sealed record RejectListingClaimRequest(string Reason);

public sealed record RevokeListingClaimRequest(string Reason);

public sealed record ListingClaimResponse(
    Guid Id,
    Guid ListingId,
    Guid ClaimantActorId,
    ClaimStateContract State,
    string EvidenceReference,
    string EvidenceDigest,
    DateTimeOffset SubmittedAtUtc,
    Guid? DecidedByActorId,
    DateTimeOffset? DecidedAtUtc,
    string? DecisionReason);

public sealed record ListingAccessGrantResponse(
    Guid Id,
    Guid ListingId,
    Guid ActorId,
    IReadOnlyList<ListingAccessScopeContract> Scopes,
    DateTimeOffset GrantedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    Guid ClaimId,
    DateTimeOffset? RevokedAtUtc);
