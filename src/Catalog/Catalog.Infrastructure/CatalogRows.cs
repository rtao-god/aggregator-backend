namespace Aggregator.Catalog.Infrastructure;

internal sealed class CatalogConfigurationRevisionRow
{
    public Guid Id { get; set; }

    public required string CatalogKey { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public required string ContentDigest { get; set; }

    public required byte[] CanonicalDocument { get; set; }

    public Guid ImportedByActorId { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; }
}

internal sealed class CatalogConfigurationValidationResultRow
{
    public Guid ConfigurationRevisionId { get; set; }

    public required string ValidatorIdentity { get; set; }

    public required string ValidatorRevision { get; set; }

    public required string SemanticFingerprint { get; set; }

    public Guid ValidatedByActorId { get; set; }

    public DateTimeOffset ValidatedAtUtc { get; set; }
}

internal sealed class ActiveCatalogConfigurationRow
{
    public required string CatalogKey { get; set; }

    public Guid ConfigurationRevisionId { get; set; }

    public DateTimeOffset ActivatedAtUtc { get; set; }

    public Guid ActivatedByActorId { get; set; }

    public long ActivationRevision { get; set; }
}

internal sealed class CatalogListingRow
{
    public Guid Id { get; set; }

    public required string CatalogKey { get; set; }

    public int SubjectKind { get; set; }

    public Guid SubjectId { get; set; }

    public int State { get; set; }

    public Guid? DraftRevisionId { get; set; }

    public Guid? ApprovedRevisionId { get; set; }

    public Guid? PublishedRevisionId { get; set; }

    public long Version { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? ArchivedAtUtc { get; set; }

    public Guid? ArchivedByActorId { get; set; }

    public string? ArchiveReason { get; set; }
}

internal sealed class CatalogListingRevisionRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public long RevisionNumber { get; set; }

    public Guid ConfigurationRevisionId { get; set; }

    public int SubjectKind { get; set; }

    public Guid SubjectId { get; set; }

    public Guid SubjectRevisionId { get; set; }

    public required string ContentDigest { get; set; }

    public Guid CreatedByActorId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class CatalogProvenanceAssertionRow
{
    public Guid Id { get; set; }

    public Guid ListingRevisionId { get; set; }

    public int SourceKind { get; set; }

    public required string SourceReference { get; set; }

    public required string EvidenceDigest { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    public DateTimeOffset RetrievedAtUtc { get; set; }
}

internal sealed class CatalogLocalizedTextRow
{
    public Guid ListingRevisionId { get; set; }

    public int FieldKind { get; set; }

    public required string Locale { get; set; }

    public int State { get; set; }

    public string? Value { get; set; }

    public Guid? AssertionId { get; set; }

    public string? MissingReason { get; set; }
}

internal sealed class CatalogCategoryAssignmentRow
{
    public Guid ListingRevisionId { get; set; }

    public required string CategoryKey { get; set; }

    public Guid AssertionId { get; set; }
}

internal sealed class CatalogAttributeValueRow
{
    public Guid ListingRevisionId { get; set; }

    public required string AttributeKey { get; set; }

    public int State { get; set; }

    public int? ValueType { get; set; }

    public string? StringValue { get; set; }

    public decimal? DecimalValue { get; set; }

    public bool? BooleanValue { get; set; }

    public string? DateValue { get; set; }

    public string? EnumValue { get; set; }

    public string? CurrencyCode { get; set; }

    public int? PriceBasis { get; set; }

    public Guid? AssertionId { get; set; }

    public string? MissingReason { get; set; }
}

internal sealed class CatalogGeographyRow
{
    public Guid ListingRevisionId { get; set; }

    public int State { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public required string AddressText { get; set; }

    public string? DistrictKey { get; set; }

    public Guid? AssertionId { get; set; }
}

internal sealed class CatalogContactRow
{
    public Guid ListingRevisionId { get; set; }

    public Guid ContactId { get; set; }

    public int Kind { get; set; }

    public required string Target { get; set; }

    public string? Label { get; set; }

    public Guid AssertionId { get; set; }
}

internal sealed class CatalogMediaRow
{
    public Guid ListingRevisionId { get; set; }

    public Guid MediaId { get; set; }

    public long MediaAggregateRevision { get; set; }

    public Guid VariantId { get; set; }

    public required string ObjectUri { get; set; }

    public required string ContentType { get; set; }

    public required string ContentDigest { get; set; }

    public int RightsBasis { get; set; }

    public int DisplayOrder { get; set; }

    public string? Caption { get; set; }

    public Guid AssertionId { get; set; }
}

internal sealed class CatalogEditorialDecisionRow
{
    public long Id { get; set; }

    public Guid ListingId { get; set; }

    public Guid RevisionId { get; set; }

    public int Decision { get; set; }

    public required string Reason { get; set; }

    public Guid ActorId { get; set; }

    public DateTimeOffset DecidedAtUtc { get; set; }
}

internal sealed class CatalogPublicationRow
{
    public Guid Id { get; set; }

    public required string CatalogKey { get; set; }

    public Guid ConfigurationRevisionId { get; set; }

    public long Sequence { get; set; }

    public required string ArtifactKey { get; set; }

    public required string ArtifactDigest { get; set; }

    public Guid CreatedByActorId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class CatalogPublicationEntryRow
{
    public Guid PublicationId { get; set; }

    public Guid ListingId { get; set; }

    public Guid ListingRevisionId { get; set; }

    public Guid SubjectRevisionId { get; set; }

    public required string ContentDigest { get; set; }
}

internal sealed class CurrentCatalogPublicationRow
{
    public required string CatalogKey { get; set; }

    public Guid PublicationId { get; set; }

    public long PublicationSequence { get; set; }

    public DateTimeOffset ActivatedAtUtc { get; set; }

    public Guid ActivatedByActorId { get; set; }
}

internal sealed class CatalogClaimRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public Guid ActorId { get; set; }

    public int Method { get; set; }

    public int State { get; set; }

    public required string EvidenceReference { get; set; }

    public required string EvidenceDigest { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }

    public Guid? DecidedByActorId { get; set; }

    public string? DecisionReason { get; set; }
}

internal sealed class CatalogListingAccessGrantRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public Guid ActorId { get; set; }

    public int State { get; set; }

    public long AggregateRevision { get; set; }

    public DateTimeOffset GrantedAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }
}

internal sealed class CatalogListingAccessScopeRow
{
    public Guid GrantId { get; set; }

    public int Scope { get; set; }
}

internal sealed class CatalogListingDisputeRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public int State { get; set; }

    public required string OpenReason { get; set; }

    public Guid OpenedByActorId { get; set; }

    public DateTimeOffset OpenedAtUtc { get; set; }

    public string? ResolutionReason { get; set; }

    public Guid? ResolvedByActorId { get; set; }

    public DateTimeOffset? ResolvedAtUtc { get; set; }

    public long AggregateRevision { get; set; }
}

internal sealed class CatalogPublicationOperationRow
{
    public Guid Id { get; set; }

    public Guid PublicationId { get; set; }

    public long PublicationSequence { get; set; }

    public required string CatalogKey { get; set; }

    public Guid ActorId { get; set; }

    public required string IdempotencyKey { get; set; }

    public required byte[] RequestDocument { get; set; }

    public required string RequestDigest { get; set; }

    public required string CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public int State { get; set; }

    public int Attempt { get; set; }

    public Guid? LeaseToken { get; set; }

    public string? LeasedBy { get; set; }

    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }

    public DateTimeOffset? NextAttemptAtUtc { get; set; }

    public Guid? ResultPublicationId { get; set; }

    public string? FailureOwner { get; set; }

    public string? FailureCode { get; set; }

    public string? FailureDetail { get; set; }

    public string? FailureRequiredAction { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class CatalogOutboxRow
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
