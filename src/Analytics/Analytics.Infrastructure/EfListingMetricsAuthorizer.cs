using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Analytics.Infrastructure;

/// <summary>Authorizes owner metrics from active, unexpired Analytics-local Catalog grant projections.</summary>
public sealed class EfListingMetricsAuthorizer(
    AnalyticsAccessProjectionDbContext dbContext,
    TimeProvider timeProvider) : IListingMetricsAuthorizer
{
    public async Task AuthorizeAsync(
        Guid actorId,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        AnalyticsDomainRules.RequireIdentifier(actorId, nameof(actorId));
        AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
        var nowUtc = timeProvider.GetUtcNow();
        AnalyticsDomainRules.RequireUtc(nowUtc, nameof(nowUtc));
        var authorized = await dbContext.ListingAccessProjections
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.ActorId == actorId &&
                    row.ListingId == listingId &&
                    row.CanViewAnalytics &&
                    row.RevokedAtUtc == null &&
                    (row.ExpiresAtUtc == null || row.ExpiresAtUtc > nowUtc),
                cancellationToken);
        if (authorized)
        {
            return;
        }

        throw new AnalyticsCommandException(
            "Analytics.AccessProjection",
            "ANALYTICS_LISTING_METRICS_FORBIDDEN",
            403,
            "The actor has no active local Analytics permission for this listing.",
            "Verify the Catalog listing access grant and consume its exact projection revision.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["actorId"] = actorId,
                ["listingId"] = listingId,
                ["evaluatedAtUtc"] = nowUtc,
            });
    }
}
