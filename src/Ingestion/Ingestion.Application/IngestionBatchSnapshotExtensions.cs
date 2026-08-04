using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public static class IngestionBatchSnapshotExtensions
{
    public static ImportBatch ToDomain(this IngestionBatchSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ImportBatch.Restore(
            snapshot.Id,
            snapshot.ProducerIdentity,
            snapshot.ProducerBuild,
            snapshot.CollectorExportId,
            snapshot.CollectorExportDigest,
            snapshot.TargetSiteKey,
            snapshot.TargetCatalogKey,
            snapshot.TargetCatalogConfigurationRevisionId,
            snapshot.ExpectedItemCount,
            snapshot.ManifestDigest,
            snapshot.ItemIndexDigest,
            snapshot.PayloadDigest,
            snapshot.PayloadObjectKey,
            snapshot.PayloadObjectDigest,
            snapshot.PayloadObjectSize,
            snapshot.PayloadContentType,
            snapshot.RegisteredAtUtc,
            snapshot.LastChangedAtUtc,
            snapshot.State,
            snapshot.AggregateRevision,
            snapshot.AcceptedItemCount,
            snapshot.ReviewRequiredItemCount,
            snapshot.RejectedItemCount,
            snapshot.FailureCode);
    }
}
