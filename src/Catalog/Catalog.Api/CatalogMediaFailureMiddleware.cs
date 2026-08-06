using Aggregator.CatalogMedia.Application;
using Aggregator.CatalogMedia.Domain;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

internal sealed class CatalogMediaFailureMiddleware(RequestDelegate next)
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
        catch (CatalogMediaApplicationException exception)
        {
            throw new OwnerException(
                new OwnerError(
                    NormalizeOwner(exception.Owner),
                    exception.Code,
                    "Catalog media owner rejected the request",
                    exception.StatusCode,
                    exception.Message,
                    exception.RequiredAction,
                    exception.Context),
                exception);
        }
        catch (CatalogMediaDomainException exception)
        {
            throw new OwnerException(
                new OwnerError(
                    "Catalog.Media.Domain",
                    exception.Code,
                    "Catalog media transition rejected",
                    StatusCodes.Status422UnprocessableEntity,
                    exception.Message,
                    "Correct the command input or expected aggregate revision before retrying."),
                exception);
        }
    }

    private static string NormalizeOwner(string owner) =>
        owner.StartsWith("CatalogMedia", StringComparison.Ordinal)
            ? $"Catalog.Media{owner["CatalogMedia".Length..]}"
            : owner;
}
