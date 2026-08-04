using System.Diagnostics.CodeAnalysis;

namespace Aggregator.Catalog.Contracts;

public static class CatalogIngestionCommandContracts
{
    public const string UpsertDraft = "aggregator.catalog.ingestion.upsert-draft@1";
}

public enum CatalogDraftValueKindContract
{
    Text = 1,

    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "Integer is the canonical producer-owned ingestion value-kind token.")]
    Integer = 2,

    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "Decimal is the canonical producer-owned ingestion value-kind token.")]
    Decimal = 3,

    Boolean = 4,
    Date = 5,
    DateTime = 6,
    Uri = 7,
    ExternalReference = 8,
}

public sealed record CatalogDraftFieldValueContract(
    string FieldKey,
    CatalogDraftValueKindContract Kind,
    string CanonicalValue,
    string Locale,
    string SourceKey,
    string EvidenceDigest,
    string UsagePolicy);

/// <summary>Creates or advances only a Catalog draft; this contract has no publication authority.</summary>
public sealed record CatalogIngestionUpsertDraftCommand(
    Guid CommandId,
    Guid IngestionBatchId,
    string IngestionItemKey,
    string CommandDigest,
    string SiteKey,
    string CatalogKey,
    Guid ExpectedCatalogConfigurationRevisionId,
    string EntityKind,
    string SubjectNaturalKey,
    IReadOnlyList<CatalogDraftFieldValueContract> Fields,
    DateTimeOffset RequestedAtUtc,
    string CorrelationId);

public enum CatalogIngestionOutcomeStateContract
{
    DraftCreated = 1,
    DraftUpdated = 2,
    Rejected = 3,
}

public sealed record CatalogIngestionCommandOutcome(
    Guid CommandId,
    Guid IngestionBatchId,
    string IngestionItemKey,
    CatalogIngestionOutcomeStateContract State,
    Guid? ListingId,
    Guid? ListingRevisionId,
    string? FailureCode,
    string? FailureDetail,
    DateTimeOffset CompletedAtUtc);
