using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;
using Platform.ProblemDetails;

namespace Aggregator.Promotion.Api;

internal sealed class PromotionFailureMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context);
        }
        catch (OwnerException)
        {
            throw;
        }
        catch (PromotionApplicationException exception)
        {
            throw new OwnerException(
                new OwnerError(
                    exception.Owner,
                    exception.Code,
                    TitleFor(exception.StatusCode),
                    exception.StatusCode,
                    exception.Message,
                    exception.RequiredAction,
                    exception.Context),
                exception);
        }
        catch (PromotionDomainException exception)
        {
            throw new OwnerException(
                new OwnerError(
                    "Promotion.Domain",
                    exception.Code,
                    "Promotion domain command rejected",
                    StatusCodes.Status422UnprocessableEntity,
                    exception.Message,
                    "Correct the command input or expected aggregate state before retrying."),
                exception);
        }
    }

    private static string TitleFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Promotion request invalid",
        StatusCodes.Status401Unauthorized => "Promotion authentication required",
        StatusCodes.Status403Forbidden => "Promotion authorization denied",
        StatusCodes.Status404NotFound => "Promotion resource not found",
        StatusCodes.Status409Conflict => "Promotion state conflict",
        StatusCodes.Status422UnprocessableEntity => "Promotion command rejected",
        StatusCodes.Status503ServiceUnavailable => "Promotion owner unavailable",
        _ => "Promotion owner failure",
    };
}
