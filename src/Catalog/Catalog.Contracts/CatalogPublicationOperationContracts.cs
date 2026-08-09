namespace Aggregator.Catalog.Contracts;

/// <summary>Public lifecycle of one durable Catalog publication operation.</summary>
public enum CatalogOperationStateContract
{
    Pending = 1,
    Leased = 2,
    RetryWait = 3,
    Completed = 4,
    Failed = 5,
}

/// <summary>Typed terminal failure retained by the Catalog publication owner.</summary>
public sealed record CatalogOperationFailureContract(
    string Owner,
    string Code,
    string Detail,
    string RequiredAction);

/// <summary>Read-only status of one exact Catalog publication operation.</summary>
public sealed record CatalogPublicationOperationResponse(
    Guid OperationId,
    string CatalogKey,
    CatalogOperationStateContract State,
    int Attempt,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? NextAttemptAtUtc,
    Guid? PublicationId,
    CatalogOperationFailureContract? Failure);
