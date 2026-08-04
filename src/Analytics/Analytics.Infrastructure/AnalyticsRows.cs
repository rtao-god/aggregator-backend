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

    public Guid BaseProjectionId { get; set; }

    public Guid PromotionOverlayId { get; set; }

    public Guid SafetyOverlayId { get; set; }

    public Guid SourcePublicationId { get; set; }

    public required string PublicReadContentDigest { get; set; }

    public required string MembershipDigest { get; set; }

    public DateTimeOffset ActivatedAtUtc { get; set; }
}

internal sealed class AnalyticsPublicListingReferenceRow
{
    public Guid PublicReadRevisionId { get; set; }

    public AnalyticsPublicReadReferenceRow PublicReadReference { get; set; } = null!;

    public Guid ListingId { get; set; }
}

internal sealed class AnalyticsListingAccessProjectionRow
{
    public Guid ListingId { get; set; }

    public Guid ActorId { get; set; }

    public bool CanViewAnalytics { get; set; }

    public long SourceAggregateRevision { get; set; }

    public required string SourcePayloadDigest { get; set; }

    public DateTimeOffset ChangedAtUtc { get; set; }
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
