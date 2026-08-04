using Aggregator.Promotion.Application;
using Platform.ProblemDetails;

namespace Aggregator.Promotion.Api;

internal static class PromotionHttpCommandContext
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static string RequireIdempotencyKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = request.Headers[IdempotencyHeader];
        if (values.Count != 1 ||
            string.IsNullOrWhiteSpace(values[0]) ||
            values[0]!.Length > 200 ||
            values[0]!.Any(char.IsControl))
        {
            throw new OwnerException(new OwnerError(
                owner: "Promotion.Commands",
                code: "PROMOTION_IDEMPOTENCY_KEY_REQUIRED",
                title: "Promotion Idempotency-Key is required",
                status: StatusCodes.Status400BadRequest,
                detail: "A mutating Promotion request requires exactly one printable Idempotency-Key of at most 200 characters.",
                requiredAction: "Submit one stable key for this exact semantic command."));
        }

        return values[0]!.Trim();
    }

    public static PromotionCommandContext Create(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actor = PromotionActorAccessor.Require(context);
        var correlationId = context.Request.Headers["X-Correlation-Id"].SingleOrDefault();
        return PromotionCommandContext.Start(actor, correlationId);
    }
}
