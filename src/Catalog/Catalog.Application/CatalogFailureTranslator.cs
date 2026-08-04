using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Transport-neutral failure emitted from the Catalog owner boundary.</summary>
public sealed record CatalogFailure(
    string Owner,
    string Code,
    string Title,
    int StatusCode,
    string Detail,
    string RequiredAction,
    IReadOnlyDictionary<string, object?> Context);

/// <summary>Translates only known Catalog owner failures; unknown failures remain unhandled.</summary>
public static class CatalogFailureTranslator
{
    public static bool TryTranslate(Exception exception, out CatalogFailure failure)
    {
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case CatalogNotFoundException notFound:
                failure = Create(
                    "Catalog",
                    "CATALOG_RESOURCE_NOT_FOUND",
                    "Catalog resource not found",
                    404,
                    notFound.Message,
                    "Reload the referenced resource identity before retrying the command.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["resourceType"] = notFound.ResourceType,
                        ["resourceId"] = notFound.ResourceId,
                    });
                return true;
            case CatalogConcurrencyException concurrency:
                failure = Create(
                    "Catalog.Listings",
                    "LISTING_REVISION_CONFLICT",
                    "Listing revision conflict",
                    409,
                    concurrency.Message,
                    "Reload the current listing revision and resubmit against its exact version.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["listingId"] = concurrency.ListingId,
                        ["expectedVersion"] = concurrency.ExpectedVersion,
                        ["actualVersion"] = concurrency.ActualVersion,
                    });
                return true;
            case CatalogAuthorizationException authorization:
                failure = Create(
                    "Catalog.Access",
                    "LISTING_ACCESS_DENIED",
                    "Listing access denied",
                    403,
                    authorization.Message,
                    "Obtain an active listing-scoped access grant containing the required scope.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["actorId"] = authorization.ActorId,
                        ["listingId"] = authorization.ListingId,
                        ["requiredScope"] = authorization.RequiredScope.ToString(),
                    });
                return true;
            case CatalogConflictException conflict:
                failure = Create(
                    "Catalog.Commands",
                    "CATALOG_COMMAND_CONFLICT",
                    "Catalog command conflict",
                    409,
                    conflict.Message,
                    "Reload the exact owner state and retry with matching pointer and revision preconditions.");
                return true;
            case CatalogContractException contract:
                failure = Create(
                    "Catalog.Contracts",
                    contract.Code,
                    "Catalog contract rejected",
                    422,
                    contract.Message,
                    "Correct the request to satisfy the current Catalog contract and retry.");
                return true;
            case CatalogInvariantException invariant:
                failure = Create(
                    "Catalog.Domain",
                    "CATALOG_INVARIANT_VIOLATION",
                    "Catalog invariant rejected the command",
                    422,
                    invariant.Message,
                    "Correct the command data at its producing owner before retrying.");
                return true;
            case ArgumentException argument:
                failure = Create(
                    "Catalog.Transport",
                    "CATALOG_REQUEST_INVALID",
                    "Catalog request is invalid",
                    400,
                    argument.Message,
                    "Correct the malformed request value and retry.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["parameter"] = argument.ParamName,
                    });
                return true;
            default:
                failure = null!;
                return false;
        }
    }

    private static CatalogFailure Create(
        string owner,
        string code,
        string title,
        int statusCode,
        string detail,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null) =>
        new(
            owner,
            code,
            title,
            statusCode,
            detail,
            requiredAction,
            context ?? new Dictionary<string, object?>(StringComparer.Ordinal));
}
