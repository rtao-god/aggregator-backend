using System.Text.Json;

namespace Aggregator.Ingestion.Contracts;

public static class AggregatorCandidatePayloadContract
{
    public const string Identity = "aggregator-candidate-ingestion-payload";

    public const int Revision = 1;
}

public enum IngestionPackageEntityKindContract
{
    Place = 1,
    Provider = 2,
}

public enum IngestionPackageUsagePolicyContract
{
    PublicAllowed = 1,
    LinkOnly = 2,
    InternalReviewOnly = 3,
    ResearchOnly = 4,
    Forbidden = 5,
}

public enum IngestionPackageQualitySeverityContract
{
    Information = 1,
    Warning = 2,
    Blocking = 3,
}

/// <summary>
/// Exact backend-owned object payload registered by AggregatorCandidateIngestionManifest.
/// The object SHA-256 is carried by the manifest and is deliberately not self-referential here.
/// </summary>
public sealed record AggregatorCandidatePayload(
    string ContractIdentity,
    int ContractRevision,
    Guid CollectorExportId,
    string ManifestDigest,
    IReadOnlyList<AggregatorCandidatePayloadItem> Items);

public sealed record AggregatorCandidatePayloadItem(
    string ItemKey,
    int Ordinal,
    IngestionPackageEntityKindContract EntityKind,
    string ContentDigest,
    JsonElement Candidate,
    IReadOnlyList<IngestionPackageEvidenceContract> Evidence,
    IReadOnlyList<IngestionPackageQualityIssueContract> QualityIssues);

public sealed record IngestionPackageEvidenceContract(
    string Field,
    string SourceKey,
    IngestionPackageUsagePolicyContract UsagePolicy,
    string Locator,
    DateTimeOffset ObservedAtUtc,
    string EvidenceDigest);

public sealed record IngestionPackageQualityIssueContract(
    string Code,
    IngestionPackageQualitySeverityContract Severity,
    string Detail);

public sealed record IngestionPackageIndexEntryContract(
    string ItemKey,
    int Ordinal,
    string ContentDigest);
