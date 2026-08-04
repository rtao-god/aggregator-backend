using Aggregator.Ingestion.Infrastructure;

namespace Aggregator.Ingestion.Api;

internal static class IngestionHealthEndpoints
{
    public static IResult Live() =>
        Results.Ok(new
        {
            owner = "Ingestion.Runtime",
            status = "live",
        });

    public static async Task<IResult> ReadyAsync(
        IngestionReadinessProbe readinessProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readinessProbe);
        if (await readinessProbe.CanConnectAsync(cancellationToken))
        {
            return Results.Ok(new
            {
                owner = "Ingestion.Persistence",
                status = "ready",
            });
        }

        return Results.Problem(
            type: "https://errors.aggregator.local/ingestion/persistence/unavailable",
            title: "Ingestion database unavailable",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "The Ingestion API cannot reach its own database.",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["owner"] = "Ingestion.Persistence",
                ["code"] = "INGESTION_DATABASE_UNAVAILABLE",
                ["requiredAction"] =
                    "Restore the Ingestion database connection and verify the owner migration state.",
            });
    }
}
