namespace Aggregator.Ingestion.Infrastructure;

internal sealed class ImportBatchRow
{
    public Guid Id { get; set; }

    public string ProducerIdentity { get; set; } = string.Empty;

    public string ProducerBuild { get; set; } = string.Empty;

    public Guid CollectorExportId { get; set; }

    public string CollectorExportDigest { get; set; } = string.Empty;

    public string TargetSiteKey { get; set; } = string.Empty;

    public string TargetCatalogKey { get; set; } = string.Empty;

    public Guid TargetCatalogConfigurationRevisionId { get; set; }

    public int ExpectedItemCount { get; set; }

    public string ManifestDigest { get; set; } = string.Empty;

    public string ItemIndexDigest { get; set; } = string.Empty;

    public string PayloadDigest { get; set; } = string.Empty;

    public string PayloadObjectKey { get; set; } = string.Empty;

    public string PayloadObjectDigest { get; set; } = string.Empty;

    public long PayloadObjectSize { get; set; }

    public string PayloadContentType { get; set; } = string.Empty;

    public DateTimeOffset RegisteredAtUtc { get; set; }

    public DateTimeOffset LastChangedAtUtc { get; set; }

    public int State { get; set; }

    public long AggregateRevision { get; set; }

    public int AcceptedItemCount { get; set; }

    public int ReviewRequiredItemCount { get; set; }

    public int RejectedItemCount { get; set; }

    public string? FailureCode { get; set; }
}

internal sealed class ImportBatchManifestRow
{
    public Guid BatchId { get; set; }

    public string ContractIdentity { get; set; } = string.Empty;

    public int ContractRevision { get; set; }

    public byte[] CanonicalDocument { get; set; } = [];

    public string ContentDigest { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class ImportBatchSourcePolicyRow
{
    public Guid BatchId { get; set; }

    public string SourceKey { get; set; } = string.Empty;

    public string PolicyDigest { get; set; } = string.Empty;

    public int UsagePolicy { get; set; }
}

internal sealed class ImportBatchArtifactRow
{
    public Guid BatchId { get; set; }

    public int Role { get; set; }

    public string ObjectKey { get; set; } = string.Empty;

    public string ContentDigest { get; set; } = string.Empty;

    public long Size { get; set; }

    public string ContentType { get; set; } = string.Empty;
}

internal sealed class IngestionCommandRow
{
    public string Scope { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string RequestDigest { get; set; } = string.Empty;

    public Guid BatchId { get; set; }

    public byte[] ResultDocument { get; set; } = [];

    public string ResultDigest { get; set; } = string.Empty;

    public string CallerServiceIdentity { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}
