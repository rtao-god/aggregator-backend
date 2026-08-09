namespace Aggregator.Promotion.Infrastructure;

internal sealed class PromotionProductRow
{
    public Guid Id { get; set; }

    public required string ProductKey { get; set; }

    public int State { get; set; }

    public Guid CurrentRevisionId { get; set; }

    public long AggregateRevision { get; set; }
}

internal sealed class PromotionProductRevisionRow
{
    public Guid Id { get; set; }

    public Guid ProductId { get; set; }

    public long RevisionNumber { get; set; }

    public required string DisplayNamesJson { get; set; }

    public required string PresentationFeaturesJson { get; set; }

    public bool RequiresVerifiedContact { get; set; }

    public string? RequiredContactCapability { get; set; }

    public Guid CreatedByActorId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public required string ContentDigest { get; set; }
}

internal sealed class PromotionEntitlementRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public required string ProductKey { get; set; }

    public int SourceType { get; set; }

    public required string ExternalReference { get; set; }

    public DateTimeOffset StartsAtUtc { get; set; }

    public DateTimeOffset EndsAtUtc { get; set; }

    public int State { get; set; }

    public Guid CreatedByActorId { get; set; }

    public required string AuditReason { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ChangedAtUtc { get; set; }

    public long AggregateRevision { get; set; }
}

internal sealed class SponsoredPlacementRow
{
    public Guid Id { get; set; }

    public Guid EntitlementId { get; set; }

    public Guid ListingId { get; set; }

    public required string ProductKey { get; set; }

    public int State { get; set; }

    public Guid CurrentRevisionId { get; set; }

    public DateTimeOffset ChangedAtUtc { get; set; }

    public required string AuditReason { get; set; }

    public long AggregateRevision { get; set; }
}

internal sealed class SponsoredPlacementRevisionRow
{
    public Guid Id { get; set; }

    public Guid PlacementId { get; set; }

    public long RevisionNumber { get; set; }

    public required string CatalogKey { get; set; }

    public int ScopeType { get; set; }

    public required string ScopeKey { get; set; }

    public required string LocaleScopeJson { get; set; }

    public DateTimeOffset StartsAtUtc { get; set; }

    public DateTimeOffset EndsAtUtc { get; set; }

    public int PriorityBand { get; set; }

    public int CapacitySlot { get; set; }

    public required string PresentationLabelKey { get; set; }

    public Guid CreatedByActorId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public required string ContentDigest { get; set; }
}

/// <summary>Transactionally maintained current schedule rows used only for capacity exclusion.</summary>
internal sealed class SponsoredPlacementCapacityRow
{
    public Guid PlacementId { get; set; }

    public Guid PlacementRevisionId { get; set; }

    public required string CatalogKey { get; set; }

    public int ScopeType { get; set; }

    public required string ScopeKey { get; set; }

    public required string Locale { get; set; }

    public int CapacitySlot { get; set; }

    public DateTimeOffset StartsAtUtc { get; set; }

    public DateTimeOffset EndsAtUtc { get; set; }

    public int PlacementState { get; set; }
}

internal sealed class ListingPromotionEligibilityRow
{
    public required string CatalogKey { get; set; }

    public Guid ListingId { get; set; }

    public Guid? PublishedListingRevisionId { get; set; }

    public bool IsPublished { get; set; }

    public bool IsArchived { get; set; }

    public bool HasBlockingDispute { get; set; }

    public bool HasVerifiedContact { get; set; }

    public required string ContactCapabilitiesJson { get; set; }

    public required string CategoryKeysJson { get; set; }

    public string? DistrictKey { get; set; }

    public long SourceRevision { get; set; }

    public DateTimeOffset ChangedAtUtc { get; set; }

    public Guid SourceMessageId { get; set; }

    public required string SourceContractIdentity { get; set; }

    public required string SourcePayloadDigest { get; set; }

    public required string ProjectionDigest { get; set; }

    public required string CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }
}

internal sealed class PromotionCommandRow
{
    public required string Scope { get; set; }

    public required string IdempotencyKey { get; set; }

    public required string RequestDigest { get; set; }

    public required string ResultKind { get; set; }

    public required string ResultJson { get; set; }

    public required string ResultDigest { get; set; }

    public Guid ActorId { get; set; }

    public required string CorrelationId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class PromotionOutboxRow
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
