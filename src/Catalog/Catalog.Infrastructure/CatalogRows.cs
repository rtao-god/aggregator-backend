namespace Aggregator.Catalog.Infrastructure;

internal sealed class CatalogConfigurationRevisionRow
{
    public Guid Id { get; set; }

    public required string SiteKey { get; set; }

    public required string CatalogKey { get; set; }

    public required string ContentDigest { get; set; }

    public required byte[] CanonicalDocument { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset ImportedAtUtc { get; set; }
}

internal sealed class ActiveCatalogConfigurationRow
{
    public required string CatalogKey { get; set; }

    public Guid ConfigurationRevisionId { get; set; }

    public Guid ActivatedByActorId { get; set; }

    public DateTimeOffset ActivatedAtUtc { get; set; }
}

internal sealed class CatalogListingRow
{
    public Guid Id { get; set; }

    public required string CatalogKey { get; set; }

    public Guid SubjectId { get; set; }

    public Guid SubjectRevisionId { get; set; }

    public int SubjectKind { get; set; }

    public int State { get; set; }

    public long Version { get; set; }

    public long LatestRevisionNumber { get; set; }

    public Guid? CurrentDraftRevisionId { get; set; }

    public Guid? ApprovedRevisionId { get; set; }

    public Guid? PublishedRevisionId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class CatalogListingRevisionRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public long RevisionNumber { get; set; }

    public Guid ConfigurationRevisionId { get; set; }

    public Guid SubjectId { get; set; }

    public Guid SubjectRevisionId { get; set; }

    public int SubjectKind { get; set; }

    public required string ContentDigest { get; set; }

    public Guid CreatedByActorId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class CatalogProvenanceAssertionRow
{
    public Guid ListingRevisionId { get; set; }

    public Guid AssertionId { get; set; }

    public int SourceKind { get; set; }

    public required string SourceReference { get; set; }

    public DateTimeOffset ObservedAtUtc { get; set; }

    public DateTimeOffset RecordedAtUtc { get; set; }

    public int UsagePolicy { get; set; }

    public required string EvidenceDigest { get; set; }
}

internal sealed class CatalogLocalizedTextRow
{
    public Guid ListingRevisionId { get; set; }

    public required string FieldKind { get; set; }

    public required string Locale { get; set; }

    public int State { get; set; }

    public string? TextValue { get; set; }

    public Guid? AssertionId { get; set; }

    public int? MissingReason { get; set; }
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

    public int? ValueKind { get; set; }

    public bool? BooleanValue { get; set; }

    public decimal? DecimalValue { get; set; }

    public string? TextValue { get; set; }

    public string[]? TextSetValue { get; set; }

    public Guid? AssertionId { get; set; }

    public int? MissingReason { get; set; }
}

internal sealed class CatalogGeographyRow
{
    public Guid ListingRevisionId { get; set; }

    public int State { get; set; }

    public decimal? Latitude { get; set; }

    public decimal? Longitude { get; set; }

    public string? DistrictKey { get; set; }

    public Guid AssertionId { get; set; }
}

internal sealed class CatalogContactRow
{
    public Guid Id { get; set; }

    public Guid ListingRevisionId { get; set; }

    public int Kind { get; set; }

    public required string Target { get; set; }

    public string? Label { get; set; }

    public Guid AssertionId { get; set; }
}

internal sealed class CatalogMediaRow
{
    public Guid MediaId { get; set; }

    public Guid ListingRevisionId { get; set; }

    public required string ObjectUri { get; set; }

    public required string ContentType { get; set; }

    public required string ContentDigest { get; set; }

    public int RightsBasis { get; set; }

    public required string RightsReference { get; set; }

    public Guid AssertionId { get; set; }
}

internal sealed class CatalogEditorialDecisionRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public Guid RevisionId { get; set; }

    public int Kind { get; set; }

    public Guid ActorId { get; set; }

    public string? Reason { get; set; }

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

internal sealed class CatalogListingClaimRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public Guid ClaimantActorId { get; set; }

    public int State { get; set; }

    public required string EvidenceReference { get; set; }

    public required string EvidenceDigest { get; set; }

    public DateTimeOffset SubmittedAtUtc { get; set; }

    public Guid? DecidedByActorId { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }

    public string? DecisionReason { get; set; }
}

internal sealed class CatalogListingAccessGrantRow
{
    public Guid Id { get; set; }

    public Guid ListingId { get; set; }

    public Guid ActorId { get; set; }

    public DateTimeOffset GrantedAtUtc { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public Guid ClaimId { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public Guid? RevokedByActorId { get; set; }

    public string? RevocationReason { get; set; }
}

internal sealed class CatalogListingAccessScopeRow
{
    public Guid GrantId { get; set; }

    public int Scope { get; set; }
}

internal sealed class CatalogOutboxRow
{
    public Guid Id { get; set; }

    public required string EventType { get; set; }

    public int EventRevision { get; set; }

    public required string Payload { get; set; }

    public DateTimeOffset OccurredAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public int AttemptCount { get; set; }

    public string? LastError { get; set; }
}
