namespace Aggregator.Query.Contracts;

/// <summary>Overall public readiness of one Query catalog projection.</summary>
public enum PublicProjectionStatusStateContract
{
    Ready = 1,
    Degraded = 2,
    Blocked = 3,
}

/// <summary>Readiness of one independently materialized Query projection component.</summary>
public enum PublicProjectionComponentStateContract
{
    Ready = 1,
    Missing = 2,
    Stale = 3,
    Blocked = 4,
}

/// <summary>Current public-read pointer and activation evidence.</summary>
public sealed record PublicReadProjectionStatus(
    PublicProjectionComponentStateContract State,
    PublicReadMetadata? Metadata,
    long? ActivationRevision,
    DateTimeOffset? ActivatedAtUtc);

/// <summary>Current sitemap pointer bound to a public-read revision.</summary>
public sealed record PublicSitemapProjectionStatus(
    PublicProjectionComponentStateContract State,
    Guid? PublicReadRevisionId,
    int? RecordCount,
    DateTimeOffset? BuiltAtUtc,
    DateTimeOffset? ActivatedAtUtc);

/// <summary>Public-safe Query projection status for one catalog.</summary>
public sealed record PublicCatalogProjectionStatusResponse(
    string CatalogKey,
    PublicProjectionStatusStateContract State,
    string Code,
    string RequiredAction,
    PublicReadProjectionStatus PublicRead,
    PublicSitemapProjectionStatus Sitemap,
    long? CatalogSourceActivationRevision,
    DateTimeOffset? CatalogCheckpointUpdatedAtUtc,
    int ActiveReadBlockCount,
    DateTimeOffset? OldestReadBlockAtUtc);
