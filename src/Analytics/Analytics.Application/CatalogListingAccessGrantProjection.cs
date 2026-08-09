using Aggregator.Analytics.Domain;
using Aggregator.Catalog.Contracts;

namespace Aggregator.Analytics.Application;

/// <summary>Exact RabbitMQ delivery metadata and producer payload for one Catalog access-grant change.</summary>
public sealed record CatalogListingAccessGrantProjectionMessage(
    Guid MessageId,
    string RoutingKey,
    string ContractIdentity,
    string PayloadDigest,
    string CorrelationId,
    Guid? CausationId,
    CatalogListingAccessGrantChanged Event);

/// <summary>One exact Catalog grant revision projected locally for Analytics report authorization.</summary>
public sealed record ListingMetricsAccessProjection
{
    private ListingMetricsAccessProjection(
        Guid grantId,
        Guid listingId,
        Guid actorId,
        bool canViewAnalytics,
        DateTimeOffset grantedAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset? revokedAtUtc,
        long sourceAggregateRevision,
        string sourcePayloadDigest,
        DateTimeOffset changedAtUtc)
    {
        GrantId = grantId;
        ListingId = listingId;
        ActorId = actorId;
        CanViewAnalytics = canViewAnalytics;
        GrantedAtUtc = grantedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        RevokedAtUtc = revokedAtUtc;
        SourceAggregateRevision = sourceAggregateRevision;
        SourcePayloadDigest = sourcePayloadDigest;
        ChangedAtUtc = changedAtUtc;
    }

    public Guid GrantId { get; }

    public Guid ListingId { get; }

    public Guid ActorId { get; }

    public bool CanViewAnalytics { get; }

    public DateTimeOffset GrantedAtUtc { get; }

    public DateTimeOffset? ExpiresAtUtc { get; }

    public DateTimeOffset? RevokedAtUtc { get; }

    public long SourceAggregateRevision { get; }

    public string SourcePayloadDigest { get; }

    public DateTimeOffset ChangedAtUtc { get; }

    public static ListingMetricsAccessProjection Create(
        Guid grantId,
        Guid listingId,
        Guid actorId,
        bool canViewAnalytics,
        DateTimeOffset grantedAtUtc,
        DateTimeOffset? expiresAtUtc,
        DateTimeOffset? revokedAtUtc,
        long sourceAggregateRevision,
        string sourcePayloadDigest,
        DateTimeOffset changedAtUtc)
    {
        AnalyticsDomainRules.RequireIdentifier(grantId, nameof(grantId));
        AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
        AnalyticsDomainRules.RequireIdentifier(actorId, nameof(actorId));
        AnalyticsDomainRules.RequireUtc(grantedAtUtc, nameof(grantedAtUtc));
        if (expiresAtUtc is not null)
        {
            AnalyticsDomainRules.RequireUtc(expiresAtUtc.Value, nameof(expiresAtUtc));
            if (expiresAtUtc <= grantedAtUtc)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_EXPIRATION_INVALID",
                    "Listing access expiration must follow grant creation.");
            }
        }

        if (revokedAtUtc is not null)
        {
            AnalyticsDomainRules.RequireUtc(revokedAtUtc.Value, nameof(revokedAtUtc));
            if (revokedAtUtc < grantedAtUtc)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_REVOCATION_TIME_INVALID",
                    "Listing access revocation cannot precede grant creation.");
            }

            if (canViewAnalytics)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_ACCESS_REVOKED_PERMISSION_INVALID",
                    "A revoked listing access grant cannot authorize Analytics reports.");
            }
        }

        if (sourceAggregateRevision <= 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_REVISION_INVALID",
                "Listing access source revision must be positive.");
        }

        var normalizedDigest = AnalyticsDomainRules.RequireDigest(
            sourcePayloadDigest,
            nameof(sourcePayloadDigest));
        AnalyticsDomainRules.RequireUtc(changedAtUtc, nameof(changedAtUtc));
        if (changedAtUtc < grantedAtUtc)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_ACCESS_CHANGE_TIME_INVALID",
                "Listing access change cannot precede grant creation.");
        }

        return new ListingMetricsAccessProjection(
            grantId,
            listingId,
            actorId,
            canViewAnalytics,
            grantedAtUtc,
            expiresAtUtc,
            revokedAtUtc,
            sourceAggregateRevision,
            normalizedDigest,
            changedAtUtc);
    }
}

/// <summary>Validated Analytics projection effect and its exact producer-message lineage.</summary>
public sealed record ListingMetricsAccessProjectionChange(
    ListingMetricsAccessProjection Projection,
    string ProjectionDigest,
    Guid MessageId,
    string RoutingKey,
    string ContractIdentity,
    string PayloadDigest,
    string CorrelationId,
    Guid? CausationId);

public enum ListingMetricsAccessProjectionDisposition
{
    Applied = 1,
    Replayed = 2,
    IgnoredStale = 3,
}

public sealed record ListingMetricsAccessProjectionResult(
    ListingMetricsAccessProjection Projection,
    ListingMetricsAccessProjectionDisposition Disposition);

/// <summary>Applies one Catalog grant event with inbox and grant-level projection state atomically.</summary>
public interface IListingMetricsAccessProjectionStore
{
    public Task<ListingMetricsAccessProjectionResult> ApplyAsync(
        ListingMetricsAccessProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken);
}
