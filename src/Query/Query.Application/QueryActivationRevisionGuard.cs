namespace Aggregator.Query.Application;

/// <summary>Validates Catalog activation ordering before Query downloads and materializes the publication artifact.</summary>
public static class QueryActivationRevisionGuard
{
    public static void EnsureCanApply(
        string catalogKey,
        long incomingActivationRevision,
        long? lastActivationRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        if (incomingActivationRevision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(incomingActivationRevision),
                incomingActivationRevision,
                "Incoming activation revision must be positive.");
        }

        if (lastActivationRevision is <= 0)
        {
            throw Failure(
                "QUERY_ACTIVATION_CHECKPOINT_INVALID",
                500,
                $"Catalog '{catalogKey}' has invalid durable activation checkpoint '{lastActivationRevision}'.",
                "Restore the Query database from an owner backup or rebuild it from exact Catalog publications.",
                catalogKey,
                expectedRevision: null,
                incomingActivationRevision,
                lastActivationRevision);
        }

        if (lastActivationRevision == long.MaxValue)
        {
            throw Failure(
                "QUERY_ACTIVATION_CHECKPOINT_EXHAUSTED",
                500,
                $"Catalog '{catalogKey}' activation checkpoint cannot advance beyond Int64 maximum.",
                "Stop the projection consumer and perform an owner-approved revision-space migration.",
                catalogKey,
                expectedRevision: null,
                incomingActivationRevision,
                lastActivationRevision);
        }

        var expectedRevision = lastActivationRevision is null
            ? 1L
            : lastActivationRevision.Value + 1L;
        if (incomingActivationRevision > expectedRevision)
        {
            throw Failure(
                "QUERY_ACTIVATION_REVISION_GAP",
                409,
                $"Catalog '{catalogKey}' expected activation revision '{expectedRevision}' but received '{incomingActivationRevision}'.",
                "Replay the missing Catalog activation revisions in order or rebuild Query from an exact owner operation.",
                catalogKey,
                expectedRevision,
                incomingActivationRevision,
                lastActivationRevision);
        }
    }

    private static QueryProjectionException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        string catalogKey,
        long? expectedRevision,
        long incomingRevision,
        long? lastRevision) =>
        new(
            "Query.Projection",
            code,
            statusCode,
            message,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = catalogKey,
                ["expectedActivationRevision"] = expectedRevision,
                ["incomingActivationRevision"] = incomingRevision,
                ["lastActivationRevision"] = lastRevision,
            });
}
