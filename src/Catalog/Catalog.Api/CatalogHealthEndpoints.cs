using Aggregator.Catalog.Infrastructure;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

internal static class CatalogHealthEndpoints
{
    public static IResult Live() =>
        Results.Ok(new
        {
            owner = "Catalog.Api",
            state = "live",
        });

    public static async Task<IResult> ReadyAsync(
        CatalogReadinessProbe probe,
        ICorrelationContextAccessor correlation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(correlation);
        var result = await probe.CheckAsync(cancellationToken);
        if (result.Ready)
        {
            return Results.Ok(new
            {
                owner = "Catalog.Persistence",
                state = result.State,
            });
        }

        return Results.Problem(
            type: "https://errors.aggregator.local/catalog/persistence/readiness-blocked",
            title: "Catalog persistence is not ready",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "Catalog cannot prove database readiness.",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["owner"] = "Catalog.Persistence",
                ["code"] = "CATALOG_DATABASE_NOT_READY",
                ["correlationId"] = correlation.CorrelationId,
                ["state"] = result.State,
                ["failureType"] = result.FailureType,
                ["requiredAction"] = "Restore the Catalog database dependency and run the owner migration command if the schema is absent.",
            });
    }
}
