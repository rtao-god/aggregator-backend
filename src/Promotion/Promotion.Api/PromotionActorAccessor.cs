using Aggregator.Promotion.Application;
using Platform.ProblemDetails;

namespace Aggregator.Promotion.Api;

internal static class PromotionActorAccessor
{
    private const string ActorIdClaim = "actor_id";

    public static PromotionActor Require(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actorIdValue = context.User.FindFirst(ActorIdClaim)?.Value;
        if (Guid.TryParse(actorIdValue, out var actorId) && actorId != Guid.Empty)
        {
            return PromotionActor.Create(actorId);
        }

        throw new OwnerException(new OwnerError(
            owner: "Promotion.Access",
            code: "PROMOTION_ACTOR_MAPPING_REQUIRED",
            title: "Promotion actor mapping is required",
            status: StatusCodes.Status403Forbidden,
            detail: "The authenticated identity has no valid internal Promotion actor mapping claim.",
            requiredAction: "Register the issuer/subject identity and issue an actor_id projection before retrying."));
    }
}
