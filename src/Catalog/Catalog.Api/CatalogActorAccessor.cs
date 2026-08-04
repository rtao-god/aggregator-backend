using Aggregator.Catalog.Application;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

internal static class CatalogActorAccessor
{
    private const string ActorIdClaim = "actor_id";

    public static CatalogActor Require(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actorIdValue = context.User.FindFirst(ActorIdClaim)?.Value;
        if (Guid.TryParse(actorIdValue, out var actorId) && actorId != Guid.Empty)
        {
            return CatalogActor.Create(actorId);
        }

        throw new OwnerException(new OwnerError(
            owner: "Catalog.Access",
            code: "ACTOR_MAPPING_REQUIRED",
            title: "Catalog actor mapping is required",
            status: StatusCodes.Status403Forbidden,
            detail: "The authenticated identity has no valid internal Catalog actor mapping claim.",
            requiredAction: "Register the issuer/subject identity with Catalog and issue an actor_id projection before retrying."));
    }
}
