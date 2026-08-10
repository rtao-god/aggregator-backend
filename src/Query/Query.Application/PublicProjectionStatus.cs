using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Exact Query-owned persistence snapshot used to derive public projection readiness.</summary>
public sealed record PublicProjectionStatusSnapshot(
    string CatalogKey,
    PublicReadRevision? PublicReadRevision,
    long? PublicReadActivationRevision,
    DateTimeOffset? PublicReadActivatedAtUtc,
    long? CatalogSourceActivationRevision,
    Guid? CatalogCheckpointPublicReadRevisionId,
    DateTimeOffset? CatalogCheckpointUpdatedAtUtc,
    int ActiveReadBlockCount,
    DateTimeOffset? OldestReadBlockAtUtc,
    Guid? SitemapPublicReadRevisionId,
    int? SitemapRecordCount,
    DateTimeOffset? SitemapBuiltAtUtc,
    DateTimeOffset? SitemapActivatedAtUtc);

/// <summary>Read-only Query persistence boundary for public projection status.</summary>
public interface IPublicProjectionStatusStore
{
    Task<PublicProjectionStatusSnapshot?> ReadAsync(
        string catalogKey,
        CancellationToken cancellationToken);
}

/// <summary>Derives a public-safe status from exact Query-owned pointer and checkpoint evidence.</summary>
public sealed class ReadPublicProjectionStatusService(
    IPublicProjectionStatusStore store)
{
    public async Task<PublicCatalogProjectionStatusResponse> ReadAsync(
        string catalogKey,
        CancellationToken cancellationToken)
    {
        var normalizedCatalogKey = RequireCatalogKey(catalogKey);
        var snapshot = await store.ReadAsync(normalizedCatalogKey, cancellationToken);
        if (snapshot is null)
        {
            throw new QueryReadException(
                "Query.ProjectionStatus",
                "QUERY_PROJECTION_STATUS_NOT_FOUND",
                404,
                $"Catalog '{normalizedCatalogKey}' has no Query projection evidence.",
                "Activate the Catalog publication and complete the Query projection before reading its status.");
        }

        ValidateSnapshot(snapshot, normalizedCatalogKey);
        return Map(snapshot);
    }

    private static PublicCatalogProjectionStatusResponse Map(
        PublicProjectionStatusSnapshot snapshot)
    {
        var revision = snapshot.PublicReadRevision;
        var blocked = snapshot.ActiveReadBlockCount > 0;
        var publicReadState = blocked
            ? PublicProjectionComponentStateContract.Blocked
            : revision is null
                ? PublicProjectionComponentStateContract.Missing
                : PublicProjectionComponentStateContract.Ready;
        var publicRead = new PublicReadProjectionStatus(
            publicReadState,
            revision is null ? null : ToMetadata(revision),
            snapshot.PublicReadActivationRevision,
            snapshot.PublicReadActivatedAtUtc);
        var sitemap = MapSitemap(snapshot, revision);

        if (blocked)
        {
            return CreateResponse(
                snapshot,
                PublicProjectionStatusStateContract.Blocked,
                "QUERY_PROJECTION_BLOCKED",
                "Complete or replay the pending Query projection operation before serving this catalog.",
                publicRead,
                sitemap);
        }

        if (sitemap.State == PublicProjectionComponentStateContract.Missing)
        {
            return CreateResponse(
                snapshot,
                PublicProjectionStatusStateContract.Degraded,
                "QUERY_SITEMAP_PROJECTION_MISSING",
                "Build and activate the sitemap revision for the current public-read revision.",
                publicRead,
                sitemap);
        }

        if (sitemap.State == PublicProjectionComponentStateContract.Stale)
        {
            return CreateResponse(
                snapshot,
                PublicProjectionStatusStateContract.Degraded,
                "QUERY_SITEMAP_PROJECTION_STALE",
                "Rebuild and activate the sitemap against the exact current public-read revision.",
                publicRead,
                sitemap);
        }

        return CreateResponse(
            snapshot,
            PublicProjectionStatusStateContract.Ready,
            "QUERY_PROJECTION_READY",
            "No recovery action is required.",
            publicRead,
            sitemap);
    }

    private static PublicCatalogProjectionStatusResponse CreateResponse(
        PublicProjectionStatusSnapshot snapshot,
        PublicProjectionStatusStateContract state,
        string code,
        string requiredAction,
        PublicReadProjectionStatus publicRead,
        PublicSitemapProjectionStatus sitemap) =>
        new(
            snapshot.CatalogKey,
            state,
            code,
            requiredAction,
            publicRead,
            sitemap,
            snapshot.CatalogSourceActivationRevision,
            snapshot.CatalogCheckpointUpdatedAtUtc,
            snapshot.ActiveReadBlockCount,
            snapshot.OldestReadBlockAtUtc);

    private static PublicSitemapProjectionStatus MapSitemap(
        PublicProjectionStatusSnapshot snapshot,
        PublicReadRevision? revision)
    {
        if (snapshot.SitemapPublicReadRevisionId is null)
        {
            return new PublicSitemapProjectionStatus(
                PublicProjectionComponentStateContract.Missing,
                null,
                null,
                null,
                null);
        }

        return new PublicSitemapProjectionStatus(
            revision is not null && snapshot.SitemapPublicReadRevisionId == revision.Id
                ? PublicProjectionComponentStateContract.Ready
                : PublicProjectionComponentStateContract.Stale,
            snapshot.SitemapPublicReadRevisionId,
            snapshot.SitemapRecordCount,
            snapshot.SitemapBuiltAtUtc,
            snapshot.SitemapActivatedAtUtc);
    }

    private static void ValidateSnapshot(
        PublicProjectionStatusSnapshot snapshot,
        string expectedCatalogKey)
    {
        if (!string.Equals(snapshot.CatalogKey, expectedCatalogKey, StringComparison.Ordinal))
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_CATALOG_MISMATCH",
                "Query projection status store returned evidence for another catalog.");
        }

        ValidateReadBlocks(snapshot);
        ValidatePublicRead(snapshot, expectedCatalogKey);
        ValidateSitemap(snapshot);

        if (snapshot.PublicReadRevision is null &&
            snapshot.ActiveReadBlockCount == 0)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_EVIDENCE_INVALID",
                "Query projection status store returned a snapshot without a public pointer or an active read block.");
        }
    }

    private static void ValidateReadBlocks(PublicProjectionStatusSnapshot snapshot)
    {
        if (snapshot.ActiveReadBlockCount < 0)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_BLOCK_COUNT_INVALID",
                "Query projection status store returned a negative read-block count.");
        }

        if ((snapshot.ActiveReadBlockCount == 0) !=
            (snapshot.OldestReadBlockAtUtc is null))
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_BLOCK_SHAPE_INVALID",
                "Query projection status store returned inconsistent read-block evidence.");
        }

        if (snapshot.OldestReadBlockAtUtc is { } blockedAtUtc)
        {
            RequireUtc(blockedAtUtc, "oldest read-block timestamp");
        }
    }

    private static void ValidatePublicRead(
        PublicProjectionStatusSnapshot snapshot,
        string expectedCatalogKey)
    {
        var revision = snapshot.PublicReadRevision;
        if (revision is null)
        {
            if (snapshot.PublicReadActivationRevision is not null ||
                snapshot.PublicReadActivatedAtUtc is not null ||
                snapshot.CatalogSourceActivationRevision is not null ||
                snapshot.CatalogCheckpointPublicReadRevisionId is not null ||
                snapshot.CatalogCheckpointUpdatedAtUtc is not null)
            {
                throw StoreFailure(
                    "QUERY_PROJECTION_STATUS_POINTER_SHAPE_INVALID",
                    "Query projection status store returned pointer or checkpoint fields without a public-read revision.");
            }

            return;
        }

        if (!string.Equals(revision.CatalogKey, expectedCatalogKey, StringComparison.Ordinal))
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_REVISION_CATALOG_MISMATCH",
                "Current public-read revision belongs to another catalog.");
        }

        if (snapshot.PublicReadActivationRevision is not { } publicReadActivationRevision ||
            publicReadActivationRevision < 1 ||
            snapshot.PublicReadActivatedAtUtc is not { } publicReadActivatedAtUtc ||
            snapshot.CatalogSourceActivationRevision is not { } sourceActivationRevision ||
            sourceActivationRevision < 1 ||
            snapshot.CatalogCheckpointPublicReadRevisionId is not { } checkpointRevisionId ||
            snapshot.CatalogCheckpointUpdatedAtUtc is not { } checkpointUpdatedAtUtc)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_POINTER_SHAPE_INVALID",
                "Current public-read revision lacks its exact activation or source-checkpoint evidence.");
        }

        RequireUtc(publicReadActivatedAtUtc, "public-read activation timestamp");
        RequireUtc(checkpointUpdatedAtUtc, "Catalog source-checkpoint timestamp");
        if (publicReadActivatedAtUtc < revision.CreatedAtUtc)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_ACTIVATION_TIME_INVALID",
                "Public-read pointer predates its immutable revision.");
        }

        if (checkpointRevisionId != revision.Id)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_CHECKPOINT_MISMATCH",
                "Catalog source checkpoint does not reference the current public-read revision.");
        }
    }

    private static void ValidateSitemap(PublicProjectionStatusSnapshot snapshot)
    {
        var sitemapFieldsPresent =
            snapshot.SitemapPublicReadRevisionId is not null ||
            snapshot.SitemapRecordCount is not null ||
            snapshot.SitemapBuiltAtUtc is not null ||
            snapshot.SitemapActivatedAtUtc is not null;
        if (!sitemapFieldsPresent)
        {
            return;
        }

        if (snapshot.PublicReadRevision is null ||
            snapshot.SitemapPublicReadRevisionId is not { } sitemapRevisionId ||
            sitemapRevisionId == Guid.Empty ||
            snapshot.SitemapRecordCount is not { } recordCount ||
            recordCount < 0 ||
            snapshot.SitemapBuiltAtUtc is not { } builtAtUtc ||
            snapshot.SitemapActivatedAtUtc is not { } activatedAtUtc)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_SITEMAP_SHAPE_INVALID",
                "Query projection status store returned incomplete sitemap pointer evidence.");
        }

        RequireUtc(builtAtUtc, "sitemap build timestamp");
        RequireUtc(activatedAtUtc, "sitemap activation timestamp");
        if (activatedAtUtc < builtAtUtc)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_SITEMAP_TIME_INVALID",
                "Active sitemap pointer predates its immutable revision.");
        }
    }

    private static PublicReadMetadata ToMetadata(PublicReadRevision revision) =>
        new(
            revision.Id,
            revision.BaseProjectionId,
            revision.PromotionOverlayId,
            revision.SafetyOverlayId,
            revision.SourcePublicationId,
            revision.CreatedAtUtc);

    private static string RequireCatalogKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new QueryReadException(
                "Query.ProjectionStatus",
                "QUERY_CATALOG_KEY_REQUIRED",
                400,
                "Catalog key is required.",
                "Submit a non-empty Catalog key.");
        }

        var normalized = value.Trim();
        if (normalized.Length > 200 || normalized.Any(char.IsControl))
        {
            throw new QueryReadException(
                "Query.ProjectionStatus",
                "QUERY_CATALOG_KEY_INVALID",
                400,
                "Catalog key is outside the supported public contract.",
                "Submit the exact Catalog key emitted by the active product configuration.");
        }

        return normalized;
    }

    private static void RequireUtc(DateTimeOffset value, string field)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw StoreFailure(
                "QUERY_PROJECTION_STATUS_TIME_NOT_UTC",
                $"Query projection status store returned a non-UTC {field}.");
        }
    }

    private static QueryReadException StoreFailure(string code, string message) =>
        new(
            "Query.ProjectionStatus",
            code,
            500,
            message,
            "Inspect query_db pointer, checkpoint, block, and sitemap revision evidence before serving projection status.");
}
