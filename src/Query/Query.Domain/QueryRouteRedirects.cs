namespace Aggregator.Query.Domain;

/// <summary>Immutable permanent redirect retained by one exact public-read revision.</summary>
public sealed record QueryRouteRedirectDocument
{
    private QueryRouteRedirectDocument(
        QuerySeoCatalogKey catalogKey,
        QuerySeoLocale locale,
        QuerySeoPath sourcePath,
        QuerySeoPath targetPath,
        Guid sourcePublicationId,
        string reason,
        DateTimeOffset createdAtUtc)
    {
        CatalogKey = catalogKey;
        Locale = locale;
        SourcePath = sourcePath;
        TargetPath = targetPath;
        SourcePublicationId = sourcePublicationId;
        Reason = reason;
        CreatedAtUtc = createdAtUtc;
    }

    public QuerySeoCatalogKey CatalogKey { get; }

    public QuerySeoLocale Locale { get; }

    public QuerySeoPath SourcePath { get; }

    public QuerySeoPath TargetPath { get; }

    public Guid SourcePublicationId { get; }

    public string Reason { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static QueryRouteRedirectDocument CreatePermanent(
        string catalogKey,
        string locale,
        string sourcePath,
        string targetPath,
        Guid sourcePublicationId,
        string reason,
        DateTimeOffset createdAtUtc)
    {
        var normalizedSource = QuerySeoPath.CreateIndexable(sourcePath, nameof(sourcePath));
        var normalizedTarget = QuerySeoPath.CreateIndexable(targetPath, nameof(targetPath));
        if (string.Equals(
                normalizedSource.Value,
                normalizedTarget.Value,
                StringComparison.Ordinal))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_REDIRECT_SELF_TARGET",
                "A permanent redirect cannot target its own source path.");
        }

        if (sourcePublicationId == Guid.Empty)
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_REDIRECT_PUBLICATION_INVALID",
                "A permanent redirect requires the exact source publication identity.");
        }

        if (string.IsNullOrWhiteSpace(reason) ||
            reason.Length > 500 ||
            reason.Any(char.IsControl) ||
            !string.Equals(reason, reason.Trim(), StringComparison.Ordinal))
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_REDIRECT_REASON_INVALID",
                "A permanent redirect requires a bounded authored reason.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw QuerySeoFailure.Create(
                "QUERY_SEO_REDIRECT_TIME_NOT_UTC",
                "Permanent redirect creation time must be UTC.");
        }

        return new QueryRouteRedirectDocument(
            QuerySeoCatalogKey.Create(catalogKey),
            QuerySeoLocale.Create(locale),
            normalizedSource,
            normalizedTarget,
            sourcePublicationId,
            reason,
            createdAtUtc);
    }
}
