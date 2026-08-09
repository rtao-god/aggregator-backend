namespace Aggregator.Catalog.Contracts;

/// <summary>Public Catalog listing dispute lifecycle.</summary>
public enum ListingDisputeStateContract
{
    Open = 1,
    Resolved = 2,
}

/// <summary>Opens one blocking Catalog listing dispute against an exact listing version.</summary>
public sealed record OpenCatalogListingDisputeRequest(
    long ExpectedListingVersion,
    string Reason);

/// <summary>Resolves one exact Catalog listing dispute revision.</summary>
public sealed record ResolveCatalogListingDisputeRequest(
    long ExpectedDisputeRevision,
    string ResolutionReason);

/// <summary>Audit-preserving Catalog listing dispute response.</summary>
public sealed record CatalogListingDisputeResponse(
    Guid DisputeId,
    Guid ListingId,
    ListingDisputeStateContract State,
    bool BlocksPromotion,
    string OpenReason,
    Guid OpenedByActorId,
    DateTimeOffset OpenedAtUtc,
    string? ResolutionReason,
    Guid? ResolvedByActorId,
    DateTimeOffset? ResolvedAtUtc,
    long AggregateRevision);
