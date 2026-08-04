using System.ComponentModel.DataAnnotations;
using System.Text.Json;

namespace Aggregator.Catalog.Contracts;

public static class CatalogIngestionDraftContract
{
    public const string Identity = "aggregator.catalog.ingestion-draft-command@1";
}

public sealed record RegisterCatalogIngestionDraftRequest(
    [property: Required] Guid CommandId,
    [property: Required, RegularExpression("^[a-z][a-z0-9-]{0,95}$")] string CatalogKey,
    [property: Required] Guid ConfigurationRevisionId,
    [property: Required] Guid ImportBatchId,
    [property: Required, StringLength(300, MinimumLength = 1)] string ItemKey,
    [property: Range(1, 2)] int EntityKind,
    [property: Required, RegularExpression("^[0-9a-f]{64}$")] string ContentDigest,
    JsonElement CandidateDocument,
    DateTimeOffset RequestedAtUtc);

public sealed record CatalogIngestionDraftResponse(
    Guid CommandId,
    Guid DraftProposalId,
    Guid SubjectId,
    Guid ListingId,
    Guid ListingRevisionId,
    string CatalogKey,
    Guid ConfigurationRevisionId,
    Guid ImportBatchId,
    string ItemKey,
    string ContentDigest,
    DateTimeOffset CreatedAtUtc,
    bool Replayed);
