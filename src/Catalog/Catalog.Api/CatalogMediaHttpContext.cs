using Aggregator.CatalogMedia.Application;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

internal static class CatalogMediaHttpContext
{
    public static string RequireIdempotencyKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) ||
            values[0]!.Length > 200 || values[0]!.Any(char.IsControl))
        {
            throw new OwnerException(new OwnerError(
                "Catalog.Media.Commands",
                "CATALOG_MEDIA_IDEMPOTENCY_KEY_REQUIRED",
                "Catalog media Idempotency-Key is required",
                StatusCodes.Status400BadRequest,
                "A mutating media request requires exactly one printable Idempotency-Key of at most 200 characters.",
                "Submit one stable key for this exact semantic command."));
        }

        return values[0]!.Trim();
    }

    public static CatalogMediaCommandContext CreateCommandContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var actorValue = context.User.FindFirst("actor_id")?.Value;
        if (!Guid.TryParse(actorValue, out var actorId) || actorId == Guid.Empty)
        {
            throw new OwnerException(new OwnerError(
                "Catalog.Media.Access",
                "CATALOG_MEDIA_ACTOR_MAPPING_REQUIRED",
                "Catalog media actor mapping is required",
                StatusCodes.Status403Forbidden,
                "The authenticated identity has no valid internal media actor mapping.",
                "Register the identity and issue an actor_id projection before retrying."));
        }

        var correlation = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
        return CatalogMediaCommandContext.Start(
            CatalogMediaActor.Create(actorId),
            correlation.CorrelationId);
    }
}
