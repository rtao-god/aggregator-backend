namespace Aggregator.Analytics.Infrastructure;

internal sealed class AnalyticsInteractionEventRow
{
    public Guid Id { get; set; }

    public Guid ClientEventId { get; set; }

    public int EventKind { get; set; }

    public required string CatalogKey { get; set; }

    public Guid? ListingId { get; set; }

    public Guid PublicReadRevisionId { get; set; }

    public AnalyticsPublicReadReferenceRow PublicReadReference { get; set; } = null!;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public required string PageContext { get; set; }

    public int PlacementExposureKind { get; set; }

    public Guid? PlacementId { get; set; }

    public AnalyticsPublicSponsoredPlacementReferenceRow? SponsoredPlacementReference { get; set; }

    public string? PlacementScopeKey { get; set; }

    public int ReferrerClass { get; set; }

    public int ConsentMode { get; set; }

    public int QualityState { get; set; }

    public required string PayloadDigest { get; set; }
}

internal sealed class AnalyticsInteractionCampaignParameterRow
{
    public Guid EventId { get; set; }

    public AnalyticsInteractionEventRow Event { get; set; } = null!;

    public required string ParameterKey { get; set; }

    public required string ParameterValue { get; set; }
}

internal sealed class AnalyticsPublicReadReferenceRow
{
    public Guid PublicReadRevisionId { get; set; }

    public required string CatalogKey { get; set; }

    public long ActivationRevision { get; set; }

    public Guid BaseProjectionId { get; set; }

    public Guid PromotionOverlayId { get; set; }

    public Guid SafetyOverlayId { get; set; }

    public Guid SourcePublicationId { get; set; }

    public required string PublicReadContentDigest { get; set; }

    public required string MembershipDigest { get; set; }

    public required string ProjectionDigest { get; set; }

    public DateTimeOffset ActivatedAtUtc { get; set; }
}

internal sealed class AnalyticsPublicListingReferenceRow
{
    public Guid PublicReadRevisionId { get; set; }

    public AnalyticsPublicReadReferenceRow PublicReadReference { get; set; } = null!;

    public Guid ListingId { get; set; }
}

internal sealed class AnalyticsPublicSponsoredPlacementReferenceRow
{
    public Guid PublicReadRevisionId { get; set; }

    public AnalyticsPublicReadReferenceRow PublicReadReference { get; set; } = null!;

    public AnalyticsPublicListingReferenceRow PublicListingReference { get; set; } = null!;

    public Guid PlacementId { get; set; }

    public Guid ListingId { get; set; }

    public int ScopeType { get; set; }

    public required string ScopeKey { get; set; }

    public DateTimeOffset StartsAtUtc { get; set; }

    public DateTimeOffset HardExpiryAtUtc { get; set; }
}

internal sealed class AnalyticsPublicReadActivationCheckpointRow
{
    public required string CatalogKey { get; set; }

    public long ActivationRevision { get; set; }

    public Guid PublicReadRevisionId { get; set; }

    public AnalyticsPublicReadReferenceRow PublicReadReference { get; set; } = null!;

    public required string ProjectionDigest { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class AnalyticsInboxMessageRow
{
    public Guid MessageId { get; set; }

    public required string CatalogKey { get; set; }

    public required string RoutingKey { get; set; }

    public required string ContractIdentity { get; set; }

    public required string PayloadDigest { get; set; }

    public long ActivationRevision { get; set; }

    public Guid PublicReadRevisionId { get; set; }

    public AnalyticsPublicReadReferenceRow PublicReadReference { get; set; } = null!;

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public required string CorrelationId { get; set; }

    public int Disposition { get; set; }

    public required string ResultProjectionDigest { get; set; }

    public DateTimeOffset ProcessedAtUtc { get; set; }
}

internal sealed class AnalyticsDailyListingMetricRow
{
    public DateOnly MetricDate { get; set; }

    public required string CatalogKey { get; set; }

    public Guid ListingId { get; set; }

    public required string AggregationSourceDigest { get; set; }

    public int SourceReadRevisionCount { get; set; }

    public int ReadinessState { get; set; }

    public long? OrganicImpressions { get; set; }

    public long? SponsoredImpressions { get; set; }

    public long? ListingOpens { get; set; }

    public long? WebsiteClicks { get; set; }

    public long? PhoneClicks { get; set; }

    public long? WhatsAppClicks { get; set; }

    public long? EmailClicks { get; set; }

    public long? MapClicks { get; set; }

    public long? ExternalProfileClicks { get; set; }

    public string? UnavailableReason { get; set; }
}

internal sealed class AnalyticsAggregateRunRow
{
    public Guid Id { get; set; }

    public DateOnly FromInclusive { get; set; }

    public DateOnly ToExclusive { get; set; }

    public int State { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    public string? SourceDigest { get; set; }

    public int? MaterializedDayCount { get; set; }

    public int? MaterializedMetricCount { get; set; }

    public int? RemovedStaleMetricCount { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureDetail { get; set; }

    public string? RequiredAction { get; set; }
}

internal sealed class AnalyticsAggregateRunItemRow
{
    public Guid RunId { get; set; }

    public AnalyticsAggregateRunRow Run { get; set; } = null!;

    public DateOnly MetricDate { get; set; }

    public required string SourceDigest { get; set; }

    public int MetricCount { get; set; }

    public DateTimeOffset CompletedAtUtc { get; set; }
}

internal sealed class AnalyticsAggregateReadinessRow
{
    public DateOnly MetricDate { get; set; }

    public Guid RunId { get; set; }

    public AnalyticsAggregateRunItemRow RunItem { get; set; } = null!;

    public required string SourceDigest { get; set; }

    public int MetricCount { get; set; }

    public DateTimeOffset CompletedAtUtc { get; set; }
}
