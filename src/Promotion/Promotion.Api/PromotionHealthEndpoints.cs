using Aggregator.Promotion.Infrastructure;
using Platform.ProblemDetails;

namespace Aggregator.Promotion.Api;

internal static class PromotionHealthEndpoints
{
    public static IResult Live() =>
        Results.Ok(new
        {
            owner = "Promotion.Runtime",
            state = "live",
        });

    public static async Task<IResult> ReadyAsync(
        PromotionReadinessProbe readinessProbe,
        ICorrelationContextAccessor correlation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readinessProbe);
        ArgumentNullException.ThrowIfNull(correlation);
        if (await readinessProbe.CanConnectAsync(cancellationToken))
        {
            return Results.Ok(new
            {
                owner = "Promotion.Persistence",
                state = "ready",
            });
        }

        return Results.Problem(
            type: "https://errors.aggregator.local/promotion/persistence/unavailable",
            title: "Promotion database unavailable",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "The Promotion API cannot reach its owner database.",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["owner"] = "Promotion.Persistence",
                ["code"] = "PROMOTION_DATABASE_UNAVAILABLE",
                ["correlationId"] = correlation.CorrelationId,
                ["requiredAction"] =
                    "Restore the Promotion database connection and verify the owner migration state.",
            });
    }
}
