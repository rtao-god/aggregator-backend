using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Analytics.Infrastructure;

internal sealed class EfAnalyticsAggregateWriter(
    AnalyticsDbContext dbContext) : IAnalyticsAggregateWriter
{
    public async Task<AnalyticsAggregateRebuildResult> RebuildAsync(
        RebuildDailyAnalyticsMetricsRequest request,
        DateTimeOffset calculatedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (calculatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw PersistenceFailure(
                "ANALYTICS_AGGREGATION_TIME_NOT_UTC",
                "Analytics aggregate completion time must use UTC.",
                "Correct the Analytics worker clock configuration.");
        }

        var rangeStartUtc = ToUtcStart(request.FromInclusive);
        var rangeEndUtc = ToUtcStart(request.ToExclusive);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var referenceRows = await dbContext.PublicReadReferences
            .AsNoTracking()
            .Where(row => row.ActivatedAtUtc < rangeEndUtc)
            .OrderBy(row => row.CatalogKey)
            .ThenBy(row => row.ActivatedAtUtc)
            .ThenBy(row => row.PublicReadRevisionId)
            .ToArrayAsync(cancellationToken);
        var referenceIds = referenceRows
            .Select(row => row.PublicReadRevisionId)
            .ToArray();
        var membershipRows = referenceIds.Length == 0
            ? []
            : await dbContext.PublicListingReferences
                .AsNoTracking()
                .Where(row => referenceIds.Contains(row.PublicReadRevisionId))
                .OrderBy(row => row.PublicReadRevisionId)
                .ThenBy(row => row.ListingId)
                .ToArrayAsync(cancellationToken);
        var eventRows = await dbContext.InteractionEvents
            .AsNoTracking()
            .Where(row =>
                row.OccurredAtUtc >= rangeStartUtc &&
                row.OccurredAtUtc < rangeEndUtc &&
                row.ListingId != null &&
                row.QualityState == (int)TrafficQualityState.Accepted)
            .OrderBy(row => row.OccurredAtUtc)
            .ThenBy(row => row.Id)
            .ToArrayAsync(cancellationToken);
        var existingRows = await dbContext.DailyListingMetrics
            .Where(row =>
                row.MetricDate >= request.FromInclusive &&
                row.MetricDate < request.ToExclusive)
            .ToArrayAsync(cancellationToken);

        var memberships = membershipRows
            .GroupBy(row => row.PublicReadRevisionId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.ListingId).ToHashSet());
        var intervals = BuildReferenceIntervals(referenceRows);
        ValidateEvents(eventRows, intervals, memberships);

        var existingByIdentity = existingRows.ToDictionary(row =>
            new MetricIdentity(row.MetricDate, row.CatalogKey, row.ListingId));
        var expectedIdentities = new HashSet<MetricIdentity>();
        var materializedMetricCount = 0;

        for (var date = request.FromInclusive; date < request.ToExclusive; date = date.AddDays(1))
        {
            var dayStartUtc = ToUtcStart(date);
            var dayEndUtc = ToUtcStart(date.AddDays(1));
            foreach (var catalogIntervals in intervals
                         .Where(interval =>
                             interval.StartsAtUtc < dayEndUtc &&
                             interval.EndsAtUtc > dayStartUtc)
                         .GroupBy(interval => interval.Reference.CatalogKey)
                         .OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var activeIntervals = catalogIntervals
                    .OrderBy(interval => interval.StartsAtUtc)
                    .ThenBy(interval => interval.Reference.PublicReadRevisionId)
                    .ToArray();
                var listingIds = activeIntervals
                    .SelectMany(interval => memberships.GetValueOrDefault(
                        interval.Reference.PublicReadRevisionId) ?? [])
                    .Distinct()
                    .Order()
                    .ToArray();
                foreach (var listingId in listingIds)
                {
                    var listingEvents = eventRows
                        .Where(row =>
                            row.ListingId == listingId &&
                            string.Equals(row.CatalogKey, catalogIntervals.Key, StringComparison.Ordinal) &&
                            row.OccurredAtUtc >= dayStartUtc &&
                            row.OccurredAtUtc < dayEndUtc)
                        .OrderBy(row => row.OccurredAtUtc)
                        .ThenBy(row => row.Id)
                        .ToArray();
                    var counts = Count(listingEvents);
                    var digest = ComputeSourceDigest(
                        date,
                        catalogIntervals.Key,
                        listingId,
                        activeIntervals,
                        listingEvents);
                    var identity = new MetricIdentity(date, catalogIntervals.Key, listingId);
                    expectedIdentities.Add(identity);
                    if (!existingByIdentity.TryGetValue(identity, out var metricRow))
                    {
                        metricRow = new AnalyticsDailyListingMetricRow
                        {
                            MetricDate = date,
                            CatalogKey = catalogIntervals.Key,
                            ListingId = listingId,
                            AggregationSourceDigest = digest,
                        };
                        dbContext.DailyListingMetrics.Add(metricRow);
                    }

                    metricRow.AggregationSourceDigest = digest;
                    metricRow.SourceReadRevisionCount = activeIntervals.Length;
                    metricRow.ReadinessState = (int)AggregateReadinessState.Complete;
                    metricRow.OrganicImpressions = counts.OrganicImpressions;
                    metricRow.SponsoredImpressions = counts.SponsoredImpressions;
                    metricRow.ListingOpens = counts.ListingOpens;
                    metricRow.WebsiteClicks = counts.WebsiteClicks;
                    metricRow.PhoneClicks = counts.PhoneClicks;
                    metricRow.WhatsAppClicks = counts.WhatsAppClicks;
                    metricRow.EmailClicks = counts.EmailClicks;
                    metricRow.MapClicks = counts.MapClicks;
                    metricRow.ExternalProfileClicks = counts.ExternalProfileClicks;
                    metricRow.UnavailableReason = null;
                    materializedMetricCount++;
                }
            }
        }

        var staleRows = existingRows
            .Where(row => !expectedIdentities.Contains(
                new MetricIdentity(row.MetricDate, row.CatalogKey, row.ListingId)))
            .ToArray();
        dbContext.DailyListingMetrics.RemoveRange(staleRows);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AnalyticsAggregateRebuildResult(
            request.FromInclusive,
            request.ToExclusive,
            materializedMetricCount,
            staleRows.Length,
            calculatedAtUtc);
    }

    private static IReadOnlyList<PublicReadInterval> BuildReferenceIntervals(
        IReadOnlyCollection<AnalyticsPublicReadReferenceRow> references)
    {
        var intervals = new List<PublicReadInterval>(references.Count);
        foreach (var catalogGroup in references
                     .GroupBy(row => row.CatalogKey)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var ordered = catalogGroup
                .OrderBy(row => row.ActivatedAtUtc)
                .ThenBy(row => row.PublicReadRevisionId)
                .ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                intervals.Add(new PublicReadInterval(
                    ordered[index],
                    ordered[index].ActivatedAtUtc,
                    index + 1 < ordered.Length
                        ? ordered[index + 1].ActivatedAtUtc
                        : DateTimeOffset.MaxValue));
            }
        }

        return intervals;
    }

    private static void ValidateEvents(
        IReadOnlyCollection<AnalyticsInteractionEventRow> events,
        IReadOnlyCollection<PublicReadInterval> intervals,
        IReadOnlyDictionary<Guid, HashSet<Guid>> memberships)
    {
        var intervalByRevision = intervals.ToDictionary(
            interval => interval.Reference.PublicReadRevisionId);
        foreach (var interaction in events)
        {
            if (!intervalByRevision.TryGetValue(
                    interaction.PublicReadRevisionId,
                    out var interval) ||
                interaction.OccurredAtUtc < interval.StartsAtUtc ||
                interaction.OccurredAtUtc >= interval.EndsAtUtc)
            {
                throw PersistenceFailure(
                    "ANALYTICS_EVENT_REVISION_TIME_CORRUPT",
                    $"Interaction event '{interaction.Id:D}' refers to a public-read revision that was not active at occurrence time.",
                    "Stop aggregation and rebuild the Analytics public-reference projection and event ledger from verified sources.");
            }

            if (interaction.ListingId is not { } listingId ||
                !memberships.TryGetValue(interaction.PublicReadRevisionId, out var listingIds) ||
                !listingIds.Contains(listingId))
            {
                throw PersistenceFailure(
                    "ANALYTICS_EVENT_MEMBERSHIP_CORRUPT",
                    $"Interaction event '{interaction.Id:D}' refers to a listing outside its exact public-read membership.",
                    "Stop aggregation and repair the Analytics event/public-reference consistency invariant.");
            }
        }
    }

    private static InteractionCounts Count(
        IReadOnlyCollection<AnalyticsInteractionEventRow> events)
    {
        long organicImpressions = 0;
        long sponsoredImpressions = 0;
        long listingOpens = 0;
        long websiteClicks = 0;
        long phoneClicks = 0;
        long whatsAppClicks = 0;
        long emailClicks = 0;
        long mapClicks = 0;
        long externalProfileClicks = 0;
        foreach (var interaction in events)
        {
            switch ((InteractionEventKind)interaction.EventKind)
            {
                case InteractionEventKind.ListingImpression:
                    if ((PlacementExposureKind)interaction.PlacementExposureKind ==
                        PlacementExposureKind.Sponsored)
                    {
                        sponsoredImpressions++;
                    }
                    else
                    {
                        organicImpressions++;
                    }

                    break;
                case InteractionEventKind.ListingOpened:
                    listingOpens++;
                    break;
                case InteractionEventKind.WebsiteClicked:
                    websiteClicks++;
                    break;
                case InteractionEventKind.PhoneClicked:
                    phoneClicks++;
                    break;
                case InteractionEventKind.WhatsAppClicked:
                    whatsAppClicks++;
                    break;
                case InteractionEventKind.EmailClicked:
                    emailClicks++;
                    break;
                case InteractionEventKind.MapClicked:
                    mapClicks++;
                    break;
                case InteractionEventKind.ExternalProfileClicked:
                    externalProfileClicks++;
                    break;
                case InteractionEventKind.ClaimStarted:
                case InteractionEventKind.ClaimSubmitted:
                    break;
                case InteractionEventKind.SearchResultsViewed:
                    throw PersistenceFailure(
                        "ANALYTICS_SEARCH_EVENT_LISTING_CORRUPT",
                        $"Search-results event '{interaction.Id:D}' unexpectedly carries a listing identity.",
                        "Stop aggregation and repair the Analytics event ledger.");
                default:
                    throw PersistenceFailure(
                        "ANALYTICS_EVENT_KIND_CORRUPT",
                        $"Interaction event '{interaction.Id:D}' contains unsupported kind '{interaction.EventKind}'.",
                        "Stop aggregation and repair the Analytics event ledger from a verified contract source.");
            }
        }

        return InteractionCounts.Create(
            organicImpressions,
            sponsoredImpressions,
            listingOpens,
            websiteClicks,
            phoneClicks,
            whatsAppClicks,
            emailClicks,
            mapClicks,
            externalProfileClicks);
    }

    private static string ComputeSourceDigest(
        DateOnly date,
        string catalogKey,
        Guid listingId,
        IReadOnlyCollection<PublicReadInterval> activeIntervals,
        IReadOnlyCollection<AnalyticsInteractionEventRow> events)
    {
        var source = new StringBuilder();
        source.Append(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append('\n')
            .Append(catalogKey)
            .Append('\n')
            .Append(listingId.ToString("D"))
            .Append('\n');
        foreach (var interval in activeIntervals
                     .OrderBy(item => item.StartsAtUtc)
                     .ThenBy(item => item.Reference.PublicReadRevisionId))
        {
            source.Append(interval.Reference.PublicReadRevisionId.ToString("D"))
                .Append('|')
                .Append(interval.Reference.PublicReadContentDigest)
                .Append('|')
                .Append(interval.Reference.MembershipDigest)
                .Append('\n');
        }

        foreach (var interaction in events
                     .OrderBy(item => item.OccurredAtUtc)
                     .ThenBy(item => item.Id))
        {
            source.Append(interaction.Id.ToString("D"))
                .Append('|')
                .Append(interaction.PayloadDigest)
                .Append('|')
                .Append(interaction.QualityState.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    private static DateTimeOffset ToUtcStart(DateOnly date) =>
        new(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private static AnalyticsCommandException PersistenceFailure(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Analytics.Persistence",
            code,
            500,
            message,
            requiredAction);

    private sealed record PublicReadInterval(
        AnalyticsPublicReadReferenceRow Reference,
        DateTimeOffset StartsAtUtc,
        DateTimeOffset EndsAtUtc);

    private sealed record MetricIdentity(
        DateOnly Date,
        string CatalogKey,
        Guid ListingId);
}
