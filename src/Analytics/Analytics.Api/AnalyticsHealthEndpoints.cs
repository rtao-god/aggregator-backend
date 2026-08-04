using Aggregator.Analytics.Infrastructure;
using Platform.ProblemDetails;

namespace Aggregator.Analytics.Api;

internal static class AnalyticsHealthEndpoints
{
    public static IResult Live() =>
        Results.Ok(new
        {
            owner = "Analytics.Runtime",
            state = "live",
        });

    public static async Task<IResult> ReadyAsync(
        AnalyticsReadinessProbe readinessProbe,
        ICorrelationContextAccessor correlation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readinessProbe);
        ArgumentNullException.ThrowIfNull(correlation);
        if (await readinessProbe.CanConnectAsync(cancellationToken))
        {
            return Results.Ok(new
            {
                owner = "Analytics.Persistence",
                state = "ready",
            });
        }

        return Results.Problem(
            type: "https://errors.aggregator.local/analytics/persistence/unavailable",
            title: "Analytics database unavailable",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "The Analytics API cannot reach its owner database.",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["owner"] = "Analytics.Persistence",
                ["code"] = "ANALYTICS_DATABASE_UNAVAILABLE",
                ["correlationId"] = correlation.CorrelationId,
                ["requiredAction"] =
                    "Restore the Analytics database connection and verify the owner migration state.",
            });
    }
}
