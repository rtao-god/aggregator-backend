namespace Aggregator.Ingestion.Contracts;

/// <summary>Identifies the exact backend-owned candidate ingestion wire contract.</summary>
public static class AggregatorCandidateIngestionContract
{
    public const string Identity = "aggregator-candidate-ingestion";

    public const int Revision = 1;
}

public enum IngestionEntityKindContract
{
    Organization = 1,
    Place = 2,
    Provider = 3,
}

public enum IngestionArtifactRoleContract
{
    CandidatePayload = 1,
    EvidenceIndex = 2,
}

public enum CandidateValueKindContract
{
    Boolean = 1,
    DecimalNumber = 2,
    Text = 3,
    TextSet = 4,
    DurationMinutes = 5,
}

public enum CandidateFieldStateContract
{
    Observed = 1,
    Unknown = 2,
    NotDisclosed = 3,
    NotApplicable = 4,
    Disputed = 5,
    Expired = 6,
}

public enum CandidateContactKindContract
{
    Website = 1,
    Email = 2,
    Phone = 3,
    WhatsApp = 4,
    Instagram = 5,
    ContactForm = 6,
    BookingRequestUrl = 7,
}

public enum CandidateUsagePolicyContract
{
    Publishable = 1,
    DisplayWithAttribution = 2,
    LinkOnly = 3,
    OwnerAuthorized = 4,
    InternalReviewOnly = 5,
    ResearchOnly = 6,
    Forbidden = 7,
}

public enum CandidateGeographyStateContract
{
    ProposedPoint = 1,
    RemoteOnly = 2,
    Unresolved = 3,
}

public enum CandidateRelationshipKindContract
{
    RegularWorkplace = 1,
    AvailableAt = 2,
    AffiliatedWith = 3,
    OperatedBy = 4,
}

public enum IngestionQualityStateContract
{
    Passed = 1,
    ReviewRequired = 2,
    Blocked = 3,
}

public enum IngestionQualitySeverityContract
{
    Blocking = 1,
    Warning = 2,
    Information = 3,
}

public sealed record IngestionPackageArtifactContract(
    IngestionArtifactRoleContract Role,
    string ObjectKey,
    string ContentDigest,
    long Size,
    string ContentType);

public sealed record IngestionSourcePolicyReferenceContract(
    string SourceKey,
    string PolicyDigest,
    CandidateUsagePolicyContract UsagePolicy);

public sealed record AggregatorCandidateIngestionManifest(
    string ContractIdentity,
    int ContractRevision,
    string ProducerIdentity,
    string ProducerBuild,
    Guid CollectorExportId,
    string CollectorExportDigest,
    string TargetSiteKey,
    string TargetCatalogKey,
    Guid TargetCatalogConfigurationRevisionId,
    DateTimeOffset CreatedAtUtc,
    int ItemCount,
    string ItemIndexDigest,
    string PayloadDigest,
    IReadOnlyList<IngestionSourcePolicyReferenceContract> SourcePolicies,
    IReadOnlyList<IngestionPackageArtifactContract> Artifacts);

public sealed record IngestionSubjectProposalContract(
    string SourceSubjectKey,
    string? OfficialDomain,
    string? NormalizedPhoneHash,
    string? NormalizedAddressKey,
    IReadOnlyList<string> ExternalIdentityKeys);

public sealed record LocalizedCandidateTextContract(
    string Locale,
    CandidateFieldStateContract State,
    string? Value,
    string FieldPath);

public sealed record CandidateTypedValueContract(
    CandidateValueKindContract Kind,
    bool? BooleanValue,
    decimal? DecimalValue,
    string? TextValue,
    IReadOnlyList<string>? TextSetValue);

public sealed record CandidateAttributeProposalContract(
    string AttributeKey,
    CandidateFieldStateContract State,
    CandidateTypedValueContract? Value,
    string FieldPath);

public sealed record CandidateContactProposalContract(
    CandidateContactKindContract Kind,
    CandidateFieldStateContract State,
    string? Target,
    string? Label,
    string FieldPath);

public sealed record CandidateExternalReferenceProposalContract(
    string SourceSystem,
    string ExternalId,
    string OutboundUrl,
    string Purpose,
    CandidateUsagePolicyContract UsagePolicy,
    string FieldPath);

public sealed record CandidateGeographyProposalContract(
    CandidateGeographyStateContract State,
    decimal? Latitude,
    decimal? Longitude,
    string? CountryCode,
    string? DistrictKey,
    string FieldPath);

public sealed record CandidateRelationshipProposalContract(
    CandidateRelationshipKindContract Kind,
    Guid RelatedCollectorCandidateId,
    long RelatedCollectorCandidateRevision,
    string FieldPath);

public sealed record CandidateProvenanceReferenceContract(
    Guid ReferenceId,
    string FieldPath,
    string SourceKey,
    string SourceExternalId,
    string SourceUrl,
    DateTimeOffset ObservedAtUtc,
    CandidateUsagePolicyContract UsagePolicy,
    string EvidenceDigest,
    string? Attribution);

public sealed record IngestionQualityIssueContract(
    string Code,
    IngestionQualitySeverityContract Severity,
    string RequiredAction);

public sealed record IngestionQualitySummaryContract(
    IngestionQualityStateContract State,
    IReadOnlyList<IngestionQualityIssueContract> Issues);

public sealed record CollectorReviewReferenceContract(
    Guid DecisionId,
    string DecisionKind,
    string DecisionDigest,
    DateTimeOffset DecidedAtUtc);

public sealed record IngestionItemContract(
    string ItemKey,
    Guid CollectorCandidateId,
    long CollectorCandidateRevision,
    IngestionEntityKindContract EntityKind,
    IngestionSubjectProposalContract SubjectProposal,
    IReadOnlyList<LocalizedCandidateTextContract> LocalizedNames,
    IReadOnlyList<string> CategoryProposals,
    IReadOnlyList<CandidateAttributeProposalContract> TypedAttributeProposals,
    IReadOnlyList<CandidateContactProposalContract> ContactProposals,
    IReadOnlyList<CandidateExternalReferenceProposalContract> ExternalReferenceProposals,
    CandidateGeographyProposalContract? GeographyProposal,
    IReadOnlyList<CandidateRelationshipProposalContract> RelationshipProposals,
    IReadOnlyList<CandidateProvenanceReferenceContract> ProvenanceReferences,
    IngestionQualitySummaryContract QualitySummary,
    IReadOnlyList<CollectorReviewReferenceContract> CollectorReviewReferences,
    string ContentDigest);

public sealed record AggregatorCandidateIngestionPackage(
    AggregatorCandidateIngestionManifest Manifest,
    IReadOnlyList<IngestionItemContract> Items);
