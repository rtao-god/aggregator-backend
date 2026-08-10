using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>Builds one deterministic owner-authorized summary without treating unavailable days as zero.</summary>
public sealed class ReadListingMetricsSummaryService(
    ReadListingMetricsRangeService rangeService)
{
    public async Task<ListingMetricsSummaryResponse> ReadAsync(
        Guid actorId,
        string catalogKey,
        Guid listingId,
        DailyMetricsRangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var metrics = await rangeService.ReadAsync(
            actorId,
            catalogKey,
            listingId,
            request,
            cancellationToken);
        var unavailableDays = metrics
            .Where(item => item.Readiness != AggregateReadinessState.Complete)
            .Select(item => new ListingMetricsSummaryUnavailableDay(
                item.Date,
                AnalyticsContractMapper.ToContract(item.Readiness),
                item.UnavailableReason
                    ?? throw CorruptMetric(
                        item,
                        "Unavailable daily metric has no owner reason.")))
            .OrderBy(item => item.Date)
            .ToArray();
        if (unavailableDays.Length > 0)
        {
            return new ListingMetricsSummaryResponse(
                catalogKey,
                listingId,
                request.FromInclusive,
                request.ToExclusive,
                ResolveReadiness(unavailableDays),
                AggregationSourceDigest: null,
                SourceDayCount: metrics.Count,
                Counts: null,
                unavailableDays);
        }

        var counts = SumCompleteCounts(metrics);
        return new ListingMetricsSummaryResponse(
            catalogKey,
            listingId,
            request.FromInclusive,
            request.ToExclusive,
            AggregateReadinessStateContract.Complete,
            ComputeSummaryDigest(metrics),
            metrics.Count,
            new InteractionCountsContract(
                counts.OrganicImpressions,
                counts.SponsoredImpressions,
                counts.ListingOpens,
                counts.WebsiteClicks,
                counts.PhoneClicks,
                counts.WhatsAppClicks,
                counts.EmailClicks,
                counts.MapClicks,
                counts.ExternalProfileClicks),
            UnavailableDays: []);
    }

    private static AggregateReadinessStateContract ResolveReadiness(
        IReadOnlyList<ListingMetricsSummaryUnavailableDay> unavailableDays)
    {
        if (unavailableDays.Any(item => item.Readiness == AggregateReadinessStateContract.Blocked))
        {
            return AggregateReadinessStateContract.Blocked;
        }

        if (unavailableDays.Any(item => item.Readiness == AggregateReadinessStateContract.Rebuilding))
        {
            return AggregateReadinessStateContract.Rebuilding;
        }

        if (unavailableDays.Any(item => item.Readiness == AggregateReadinessStateContract.Partial))
        {
            return AggregateReadinessStateContract.Partial;
        }

        throw new AnalyticsCommandException(
            "Analytics.Aggregates",
            "ANALYTICS_SUMMARY_READINESS_CORRUPT",
            500,
            "Listing summary contains an unsupported unavailable readiness state.",
            "Stop summary reads and repair the Analytics daily metric owner state.");
    }

    private static InteractionCounts SumCompleteCounts(
        IReadOnlyList<DailyListingMetrics> metrics)
    {
        try
        {
            checked
            {
                return InteractionCounts.Create(
                    metrics.Sum(item => RequireCounts(item).OrganicImpressions),
                    metrics.Sum(item => RequireCounts(item).SponsoredImpressions),
                    metrics.Sum(item => RequireCounts(item).ListingOpens),
                    metrics.Sum(item => RequireCounts(item).WebsiteClicks),
                    metrics.Sum(item => RequireCounts(item).PhoneClicks),
                    metrics.Sum(item => RequireCounts(item).WhatsAppClicks),
                    metrics.Sum(item => RequireCounts(item).EmailClicks),
                    metrics.Sum(item => RequireCounts(item).MapClicks),
                    metrics.Sum(item => RequireCounts(item).ExternalProfileClicks));
            }
        }
        catch (OverflowException exception)
        {
            throw new AnalyticsCommandException(
                "Analytics.Aggregates",
                "ANALYTICS_SUMMARY_COUNT_OVERFLOW",
                500,
                "Listing summary count exceeded the supported 64-bit range.",
                "Stop summary reads and investigate the exact daily metric range.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sourceDayCount"] = metrics.Count,
                    ["failureType"] = exception.GetType().FullName,
                });
        }
    }

    private static InteractionCounts RequireCounts(DailyListingMetrics item) =>
        item.Counts
        ?? throw CorruptMetric(
            item,
            "Complete daily metric has no observed counts.");

    private static string ComputeSummaryDigest(IReadOnlyList<DailyListingMetrics> metrics)
    {
        var source = new StringBuilder();
        foreach (var item in metrics.OrderBy(item => item.Date))
        {
            source.Append(item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append('|')
                .Append(item.AggregationSourceDigest)
                .Append('|')
                .Append(item.SourceReadRevisionCount.ToString(CultureInfo.InvariantCulture))
                .Append('\n');
        }

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(source.ToString())));
    }

    private static AnalyticsCommandException CorruptMetric(
        DailyListingMetrics item,
        string detail) =>
        new(
            "Analytics.Persistence",
            "ANALYTICS_DAILY_METRIC_CORRUPT",
            500,
            $"{detail} Date '{item.Date:yyyy-MM-dd}', listing '{item.ListingId:D}'.",
            "Stop metrics reads and repair the exact Analytics daily metric row.");
}
