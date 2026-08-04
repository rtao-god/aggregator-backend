using Aggregator.Catalog.Application;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

internal static class CatalogEventContextAccessor
{
    public static CatalogEventContext Require(ICorrelationContextAccessor correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        if (string.IsNullOrWhiteSpace(correlation.CorrelationId))
        {
            throw new OwnerException(new OwnerError(
                owner: "Catalog.Transport",
                code: "CORRELATION_CONTEXT_REQUIRED",
                title: "Catalog correlation context is required",
                status: StatusCodes.Status500InternalServerError,
                detail: "The Catalog command reached its transport owner without a correlation identity.",
                requiredAction: "Restore the correlation middleware before accepting Catalog commands."));
        }

        return CatalogEventContext.Create(correlation.CorrelationId);
    }
}
