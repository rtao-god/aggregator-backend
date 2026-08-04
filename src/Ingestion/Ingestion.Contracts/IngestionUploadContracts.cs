namespace Aggregator.Ingestion.Contracts;

/// <summary>Requests upload authorization for the exact registered payload object.</summary>
public sealed record PrepareIngestionUploadRequest(long ExpectedAggregateRevision);

public sealed record IngestionUploadAuthorizationDto(
    Uri UploadUri,
    string ObjectKey,
    DateTimeOffset ExpiresAtUtc,
    string ContentType,
    long MaximumSize,
    IngestionBatchDto Batch,
    bool Replayed);

/// <summary>Confirms that the exact registered payload object has been uploaded and verified.</summary>
public sealed record CompleteIngestionUploadRequest(long ExpectedAggregateRevision);

public sealed record IngestionBatchCommandResponse(
    IngestionBatchDto Batch,
    bool Replayed);
