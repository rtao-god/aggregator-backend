using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public static class IngestionBatchContractMapper
{
    public static IngestionBatchDto ToDto(IngestionBatchSnapshot batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return new IngestionBatchDto(
            batch.Id.Value,
            batch.ProducerIdentity,
            batch.ProducerBuild,
            batch.CollectorExportId,
            batch.CollectorExportDigest,
            batch.TargetSiteKey,
            batch.TargetCatalogKey,
            batch.TargetCatalogConfigurationRevisionId,
            batch.ExpectedItemCount,
            batch.ManifestDigest,
            batch.ItemIndexDigest,
            batch.PayloadDigest,
            batch.PayloadObjectKey,
            batch.PayloadObjectDigest,
            batch.PayloadObjectSize,
            batch.PayloadContentType,
            batch.RegisteredAtUtc,
            batch.LastChangedAtUtc,
            ToContract(batch.State),
            batch.AggregateRevision,
            batch.AcceptedItemCount,
            batch.ReviewRequiredItemCount,
            batch.RejectedItemCount,
            batch.FailureCode);
    }

    private static ImportBatchStateContract ToContract(ImportBatchState state) => state switch
    {
        ImportBatchState.Registered => ImportBatchStateContract.Registered,
        ImportBatchState.Uploading => ImportBatchStateContract.Uploading,
        ImportBatchState.Uploaded => ImportBatchStateContract.Uploaded,
        ImportBatchState.IntegrityChecking => ImportBatchStateContract.IntegrityChecking,
        ImportBatchState.IntegrityValid => ImportBatchStateContract.IntegrityValid,
        ImportBatchState.ItemValidation => ImportBatchStateContract.ItemValidation,
        ImportBatchState.ReviewRequired => ImportBatchStateContract.ReviewRequired,
        ImportBatchState.ReadyToCommit => ImportBatchStateContract.ReadyToCommit,
        ImportBatchState.Committing => ImportBatchStateContract.Committing,
        ImportBatchState.Committed => ImportBatchStateContract.Committed,
        ImportBatchState.PartiallyRejected => ImportBatchStateContract.PartiallyRejected,
        ImportBatchState.Superseded => ImportBatchStateContract.Superseded,
        ImportBatchState.IntegrityFailed => ImportBatchStateContract.IntegrityFailed,
        ImportBatchState.ContractRejected => ImportBatchStateContract.ContractRejected,
        ImportBatchState.BlockedByPolicy => ImportBatchStateContract.BlockedByPolicy,
        ImportBatchState.CommitFailed => ImportBatchStateContract.CommitFailed,
        ImportBatchState.Expired => ImportBatchStateContract.Expired,
        ImportBatchState.Cancelled => ImportBatchStateContract.Cancelled,
        _ => throw new IngestionApplicationException(
            "Ingestion.Contracts",
            "INGESTION_BATCH_STATE_UNSUPPORTED",
            500,
            $"Import batch state '{state}' has no transport contract mapping.",
            "Add the new owner state to the Ingestion transport contract before exposing it."),
    };
}
