using Platform.ProblemDetails;

namespace Aggregator.Analytics.Api;

internal static class AnalyticsActorAccessor
{
    private const string ActorIdClaim = "actor_id";

    public static Guid Require(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var value = context.User.FindFirst(ActorIdClaim)?.Value;
        if (Guid.TryParse(value, out var actorId) && actorId != Guid.Empty)
        {
            return actorId;
        }

        throw new OwnerException(new OwnerError(
            owner: "Analytics.Access",
            code: "ANALYTICS_ACTOR_MAPPING_REQUIRED",
            title: "Analytics actor mapping is required",
            status: StatusCodes.Status403Forbidden,
            detail: "The authenticated identity has no valid internal Analytics actor mapping claim.",
            requiredAction: "Register the issuer/subject identity and issue an actor_id projection before retrying."));
    }
}
