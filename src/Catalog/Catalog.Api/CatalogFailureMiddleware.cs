using Aggregator.Catalog.Application;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

internal sealed class CatalogFailureMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OwnerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (!CatalogFailureTranslator.TryTranslate(exception, out var failure))
            {
                throw;
            }

            throw new OwnerException(
                new OwnerError(
                    failure.Owner,
                    failure.Code,
                    failure.Title,
                    failure.StatusCode,
                    failure.Detail,
                    failure.RequiredAction,
                    failure.Context),
                exception);
        }
    }
}
