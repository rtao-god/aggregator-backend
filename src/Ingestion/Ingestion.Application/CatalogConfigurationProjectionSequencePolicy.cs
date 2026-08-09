namespace Aggregator.Ingestion.Application;

/// <summary>Minimal current-state checkpoint required to validate the next Catalog activation.</summary>
public sealed record CatalogConfigurationProjectionCheckpoint(
    string SiteKey,
    Guid ConfigurationRevisionId,
    long AggregateRevision);

/// <summary>Owns monotonic Catalog configuration activation sequencing for the Ingestion projection.</summary>
public static class CatalogConfigurationProjectionSequencePolicy
{
    public static void RequireNext(
        CatalogConfigurationProjectionCheckpoint? current,
        CatalogConfigurationProjection incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (current is null)
        {
            if (incoming.AggregateRevision != 1 || incoming.PreviousConfigurationRevisionId is not null)
            {
                throw Gap(incoming, expectedRevision: 1, actualRevision: null);
            }

            return;
        }

        if (!string.Equals(current.SiteKey, incoming.SiteKey, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_SITE_CHANGED",
                409,
                $"Catalog '{incoming.CatalogKey}' moved from site '{current.SiteKey}' to '{incoming.SiteKey}'.",
                "Correct the Catalog owner identity; a catalog cannot change its site through an activation event.",
                incoming,
                current.AggregateRevision);
        }

        if (current.ConfigurationRevisionId == Guid.Empty || current.AggregateRevision <= 0)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_CHECKPOINT_CORRUPT",
                503,
                "The current Ingestion Catalog configuration checkpoint is invalid.",
                "Rebuild the Ingestion Catalog projection from the complete producer event stream.",
                incoming,
                current.AggregateRevision);
        }

        var expectedRevision = checked(current.AggregateRevision + 1);
        if (incoming.AggregateRevision > expectedRevision)
        {
            throw Gap(incoming, expectedRevision, current.AggregateRevision);
        }

        if (incoming.AggregateRevision < expectedRevision)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_REVISION_REUSED",
                409,
                $"Catalog configuration aggregate revision '{incoming.AggregateRevision}' was received under a new message identity after revision '{current.AggregateRevision}'.",
                "Replay the exact previously accepted message or rebuild from the complete Catalog activation stream.",
                incoming,
                current.AggregateRevision);
        }

        if (incoming.PreviousConfigurationRevisionId != current.ConfigurationRevisionId)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_POINTER_CHAIN_MISMATCH",
                409,
                "Catalog configuration activation does not continue from the current Ingestion projection pointer.",
                "Replay the missing or corrected Catalog activation stream in aggregate-revision order.",
                incoming,
                current.AggregateRevision);
        }
    }

    private static IngestionApplicationException Gap(
        CatalogConfigurationProjection incoming,
        long expectedRevision,
        long? actualRevision) =>
        new(
            "Ingestion.CatalogProjection",
            "INGESTION_CATALOG_CONFIGURATION_REVISION_GAP",
            503,
            $"Catalog '{incoming.CatalogKey}' expected activation revision '{expectedRevision}' but received '{incoming.AggregateRevision}'.",
            "Replay Catalog configuration activations beginning with the next expected aggregate revision.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = incoming.CatalogKey,
                ["expectedAggregateRevision"] = expectedRevision,
                ["actualProjectedRevision"] = actualRevision,
                ["receivedAggregateRevision"] = incoming.AggregateRevision,
            });

    private static IngestionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        CatalogConfigurationProjection incoming,
        long? currentRevision) =>
        new(
            "Ingestion.CatalogProjection",
            code,
            statusCode,
            detail,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = incoming.CatalogKey,
                ["configurationRevisionId"] = incoming.ConfigurationRevisionId,
                ["receivedAggregateRevision"] = incoming.AggregateRevision,
                ["currentAggregateRevision"] = currentRevision,
                ["sourceEventId"] = incoming.SourceEventId,
            });
}
