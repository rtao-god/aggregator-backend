using System.Diagnostics.CodeAnalysis;

namespace Aggregator.Ingestion.Contracts;

public static class IngestionCandidatePayloadContract
{
    public const string Identity = "aggregator-candidate-payload";
    public const int Revision = 1;
}

public enum IngestionCandidateFieldValueKindContract
{
    Text = 1,

    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "Integer is the canonical backend ingestion value-kind token.")]
    Integer = 2,

    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "Decimal is the canonical backend ingestion value-kind token.")]
    Decimal = 3,

    Boolean = 4,
    Date = 5,
    DateTime = 6,
    Uri = 7,
    ExternalReference = 8,
}

public sealed record IngestionCandidateFieldContract(
    string FieldKey,
    IngestionCandidateFieldValueKindContract Kind,
    string CanonicalValue,
    string Locale,
    string SourceKey,
    string EvidenceDigest,
    string UsagePolicy);

public sealed record IngestionCandidatePayloadItem(
    string ItemKey,
    string EntityKind,
    string SubjectNaturalKey,
    IReadOnlyList<IngestionCandidateFieldContract> Fields);

public sealed record IngestionCandidatePayloadDocument(
    string ContractIdentity,
    int ContractRevision,
    Guid CollectorExportId,
    string CollectorExportDigest,
    IReadOnlyList<IngestionCandidatePayloadItem> Items);
