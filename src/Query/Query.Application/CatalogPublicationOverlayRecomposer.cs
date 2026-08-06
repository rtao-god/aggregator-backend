using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed record QueryBaseProjectionComponent(
    Guid Id,
    string CatalogKey,
    Guid SourcePublicationId,
    string ContentDigest);

/// <summary>
/// Composes a newly materialized Catalog base projection with the exact immutable overlays that
/// were current immediately before that publication activation.
/// </summary>
public static class CatalogPublicationOverlayRecomposer
{
    public static PublicReadRevision Compose(
        QueryBaseProjection baseProjection,
        QueryOverlayRevision promotionOverlay,
        QueryOverlayRevision safetyOverlay,
        Guid publicReadRevisionId,
        DateTimeOffset builtAtUtc)
    {
        ArgumentNullException.ThrowIfNull(baseProjection);
        return Compose(
            new QueryBaseProjectionComponent(
                baseProjection.Id,
                baseProjection.CatalogKey,
                baseProjection.SourcePublicationId,
                baseProjection.ContentDigest),
            promotionOverlay,
            safetyOverlay,
            publicReadRevisionId,
            builtAtUtc);
    }

    public static PublicReadRevision Compose(
        QueryBaseProjectionComponent baseProjection,
        QueryOverlayRevision promotionOverlay,
        QueryOverlayRevision safetyOverlay,
        Guid publicReadRevisionId,
        DateTimeOffset builtAtUtc)
    {
        ArgumentNullException.ThrowIfNull(baseProjection);
        ArgumentNullException.ThrowIfNull(promotionOverlay);
        ArgumentNullException.ThrowIfNull(safetyOverlay);
        if (baseProjection.Id == Guid.Empty ||
            baseProjection.SourcePublicationId == Guid.Empty)
        {
            throw Failure(
                "QUERY_PUBLICATION_BASE_IDENTITY_INVALID",
                "Catalog publication recomposition received an empty base or source-publication identity.");
        }

        var catalogKey = RequireText(
            baseProjection.CatalogKey,
            "QUERY_PUBLICATION_BASE_CATALOG_INVALID",
            "Catalog publication recomposition requires a non-empty catalog key.",
            200);
        var baseDigest = RequireDigest(baseProjection.ContentDigest);
        if (promotionOverlay.Kind != QueryOverlayKind.Promotion ||
            safetyOverlay.Kind != QueryOverlayKind.VisibilitySafety)
        {
            throw Failure(
                "QUERY_PUBLICATION_OVERLAY_KIND_INVALID",
                "Catalog publication recomposition requires one promotion and one visibility-safety overlay.");
        }

        if (!string.Equals(
                catalogKey,
                promotionOverlay.CatalogKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                catalogKey,
                safetyOverlay.CatalogKey,
                StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_PUBLICATION_OVERLAY_CATALOG_MISMATCH",
                "Catalog publication recomposition contains components owned by different catalogs.");
        }

        var publicReadDigest = QueryCanonicalJson.ComputeDigest(new
        {
            baseProjectionDigest = baseDigest,
            promotionOverlayDigest = promotionOverlay.ContentDigest,
            safetyOverlayDigest = safetyOverlay.ContentDigest,
            baseProjection.SourcePublicationId,
        });
        return PublicReadRevision.Restore(
            publicReadRevisionId,
            catalogKey,
            baseProjection.Id,
            promotionOverlay.Id,
            safetyOverlay.Id,
            baseProjection.SourcePublicationId,
            builtAtUtc,
            publicReadDigest);
    }

    private static string RequireDigest(string value)
    {
        var normalized = RequireText(
            value,
            "QUERY_PUBLICATION_BASE_DIGEST_INVALID",
            "Catalog publication recomposition requires a lowercase SHA-256 base digest.",
            64);
        if (normalized.Length != 64 ||
            normalized.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "QUERY_PUBLICATION_BASE_DIGEST_INVALID",
                "Catalog publication recomposition requires a lowercase SHA-256 base digest.");
        }

        return normalized;
    }

    private static string RequireText(
        string value,
        string code,
        string message,
        int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Failure(code, message);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw Failure(code, message);
        }

        return normalized;
    }

    private static QueryProjectionException Failure(string code, string message) =>
        new(
            "Query.CatalogPublicationProjection",
            code,
            500,
            message,
            "Restore the exact Query components and replay the Catalog publication event.");
}
