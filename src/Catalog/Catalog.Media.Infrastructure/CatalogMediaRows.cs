namespace Aggregator.CatalogMedia.Infrastructure;

internal sealed class CatalogMediaAssetRow
{
    public Guid Id { get; set; }
    public required string CatalogKey { get; set; }
    public int State { get; set; }
    public required string QuarantineObjectKey { get; set; }
    public required string ExpectedContentType { get; set; }
    public required string ExpectedContentDigest { get; set; }
    public long ExpectedSize { get; set; }
    public int RightsBasis { get; set; }
    public required string RightsReference { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public DateTimeOffset ChangedAtUtc { get; set; }
    public long AggregateRevision { get; set; }
    public DateTimeOffset? UploadAuthorizationExpiresAtUtc { get; set; }
    public DateTimeOffset? UploadedAtUtc { get; set; }
    public DateTimeOffset? ScannedAtUtc { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public DateTimeOffset? RightsRevokedAtUtc { get; set; }
    public Guid? RightsRevokedByActorId { get; set; }
    public string? FailureCode { get; set; }
}

internal sealed class CatalogMediaVariantRow
{
    public Guid Id { get; set; }
    public Guid AssetId { get; set; }
    public int Kind { get; set; }
    public required string ObjectKey { get; set; }
    public required string ContentType { get; set; }
    public required string ContentDigest { get; set; }
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class CatalogMediaCommandRow
{
    public required string Scope { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestDigest { get; set; }
    public Guid AssetId { get; set; }
    public required byte[] ResultDocument { get; set; }
    public required string ResultDigest { get; set; }
    public Guid ActorId { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class CatalogMediaProcessingWorkRow
{
    public Guid AssetId { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? LeasedBy { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastFailedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
}

internal sealed class CatalogMediaOutboxRow
{
    public Guid MessageId { get; set; }
    public required string RoutingKey { get; set; }
    public required string ContractIdentity { get; set; }
    public required string PayloadJson { get; set; }
    public required string PayloadDigest { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string CorrelationId { get; set; }
    public Guid? CausationId { get; set; }
    public Guid? LeaseToken { get; set; }
    public string? LeasedBy { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset? DispatchedAtUtc { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? DeadLetteredAtUtc { get; set; }
    public string? DeadLetterReason { get; set; }
}
