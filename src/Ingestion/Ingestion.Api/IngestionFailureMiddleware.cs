using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Domain;
using Platform.ProblemDetails;

namespace Aggregator.Ingestion.Api;

internal sealed class IngestionFailureMiddleware(RequestDelegate next)
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
        catch (IngestionApplicationException exception)
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
        catch (IngestionDomainException exception)
        {
            throw new OwnerException(
                new OwnerError(
                    "Ingestion.Domain",
                    exception.Code,
                    "Ingestion domain command rejected",
                    StatusCodes.Status422UnprocessableEntity,
                    exception.Message,
                    "Correct the command input or expected aggregate state before retrying."),
                exception);
        }
    }

    private static string TitleFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "Ingestion request invalid",
        StatusCodes.Status401Unauthorized => "Ingestion authentication required",
        StatusCodes.Status403Forbidden => "Ingestion authorization denied",
        StatusCodes.Status404NotFound => "Ingestion resource not found",
        StatusCodes.Status409Conflict => "Ingestion state conflict",
        StatusCodes.Status422UnprocessableEntity => "Ingestion command rejected",
        StatusCodes.Status503ServiceUnavailable => "Ingestion owner unavailable",
        _ => "Ingestion owner failure",
    };
}
