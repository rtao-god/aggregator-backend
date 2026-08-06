namespace Aggregator.Catalog.Contracts;

public enum PublicVisibilitySuppressionTargetKindContract
{
    Listing = 1,
    Media = 2,
    Contact = 3,
    Route = 4,
    ExternalReference = 5,
}

public enum PublicVisibilitySuppressionResponseModeContract
{
    HideAsNotFound = 1,
    Gone = 2,
    TemporarilyUnavailable = 3,
    OmitChildElement = 4,
}

public enum PublicVisibilitySuppressionStateContract
{
    Requested = 1,
    Active = 2,
    Resolved = 3,
}

/// <summary>Exact public target removed by one Catalog-owned safety suppression.</summary>
public sealed record PublicVisibilitySuppressionTargetContract(
    PublicVisibilitySuppressionTargetKindContract Kind,
    Guid? ListingId,
    string TargetKey);

/// <summary>Creates and immediately activates one emergency public-visibility suppression.</summary>
public sealed record CreatePublicVisibilitySuppressionRequest(
    PublicVisibilitySuppressionTargetContract Target,
    string PublicReasonClass,
    string PrivateEvidenceReference,
    PublicVisibilitySuppressionResponseModeContract ResponseMode,
    DateTimeOffset? ExpiresAtUtc,
    string Reason);

/// <summary>Resolves one active suppression against its exact aggregate revision.</summary>
public sealed record ResolvePublicVisibilitySuppressionRequest(
    long ExpectedRevision,
    string Reason);

/// <summary>Catalog administrative representation; private evidence never enters Query events.</summary>
public sealed record PublicVisibilitySuppressionResponse(
    Guid Id,
    string CatalogKey,
    PublicVisibilitySuppressionTargetContract Target,
    string PublicReasonClass,
    string PrivateEvidenceReference,
    PublicVisibilitySuppressionResponseModeContract ResponseMode,
    DateTimeOffset StartsAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    PublicVisibilitySuppressionStateContract State,
    long Revision,
    Guid ChangedByActorId,
    string TransitionReason,
    DateTimeOffset ChangedAtUtc);
