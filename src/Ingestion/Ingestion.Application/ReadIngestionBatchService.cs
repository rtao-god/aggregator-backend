using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Aggregator.Ingestion.Application;

public sealed class ReadIngestionBatchService
{
    private readonly IIngestionBatchRepository _repository;

    public ReadIngestionBatchService(IIngestionBatchRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<IngestionBatchDto?> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        if (batchId == Guid.Empty)
        {
            throw new IngestionApplicationException(
                "Ingestion.Batches",
                "INGESTION_BATCH_ID_REQUIRED",
                400,
                "A non-empty import batch ID is required.",
                "Use the exact ImportBatchId returned by registration.");
        }

        var batch = await _repository.ReadAsync(
            ImportBatchId.Create(batchId),
            cancellationToken);
        return batch is null ? null : IngestionBatchContractMapper.ToDto(batch);
    }
}
