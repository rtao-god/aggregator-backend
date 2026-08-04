using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>Returns owner-authorized daily metrics only when the requested aggregate range is explicit and complete.</summary>
public sealed class ReadDailyListingMetricsService(
    IDailyListingMetricsStore metricsStore,
    IListingMetricsAuthorizer authorizer)
{
    public async Task<IReadOnlyList<DailyListingMetricsResponse>> ReadAsync(
        Guid actorId,
        string catalogKey,
        Guid listingId,
        DailyMetricsRangeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string normalizedCatalogKey;
        try
        {
            AnalyticsDomainRules.RequireIdentifier(actorId, nameof(actorId));
            AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
            normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
            request.Validate();
        }
        catch (AnalyticsDomainException exception)
        {
            throw InvalidRequest(exception.Code, exception.Message);
        }
        catch (ArgumentException exception)
        {
            throw InvalidRequest("ANALYTICS_METRICS_RANGE_INVALID", exception.Message);
        }

        await authorizer.AuthorizeAsync(actorId, listingId, cancellationToken);
        var metrics = await metricsStore.GetRangeAsync(
            normalizedCatalogKey,
            listingId,
            request.FromInclusive,
            request.ToExclusive,
            cancellationToken);
        ArgumentNullException.ThrowIfNull(metrics);

        var byDate = new Dictionary<DateOnly, DailyListingMetrics>();
        foreach (var item in metrics)
        {
            if (!string.Equals(item.CatalogKey, normalizedCatalogKey, StringComparison.Ordinal) ||
                item.ListingId != listingId ||
                item.Date < request.FromInclusive ||
                item.Date >= request.ToExclusive)
            {
                throw CorruptStore(
                    "Analytics metrics store returned a row outside the requested owner scope.",
                    normalizedCatalogKey,
                    listingId,
                    request,
                    item.Date);
            }

            if (!byDate.TryAdd(item.Date, item))
            {
                throw CorruptStore(
                    $"Analytics metrics store returned duplicate aggregate date '{item.Date:yyyy-MM-dd}'.",
                    normalizedCatalogKey,
                    listingId,
                    request,
                    item.Date);
            }
        }

        var missingDates = EnumerateDates(request.FromInclusive, request.ToExclusive)
            .Where(date => !byDate.ContainsKey(date))
            .ToArray();
        if (missingDates.Length > 0)
        {
            throw new AnalyticsCommandException(
                "Analytics.Aggregates",
                "ANALYTICS_AGGREGATE_COVERAGE_INCOMPLETE",
                503,
                "The requested metrics range is not fully materialized.",
                "Run or resume the Analytics aggregate owner for the missing dates before reading this range.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = normalizedCatalogKey,
                    ["listingId"] = listingId,
                    ["fromInclusive"] = request.FromInclusive,
                    ["toExclusive"] = request.ToExclusive,
                    ["missingDates"] = missingDates.Select(date => date.ToString("yyyy-MM-dd", null)).ToArray(),
                });
        }

        return byDate
            .OrderBy(item => item.Key)
            .Select(item => AnalyticsContractMapper.ToResponse(item.Value))
            .ToArray();
    }

    private static IEnumerable<DateOnly> EnumerateDates(DateOnly fromInclusive, DateOnly toExclusive)
    {
        for (var date = fromInclusive; date < toExclusive; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static AnalyticsCommandException InvalidRequest(string code, string message) =>
        new(
            "Analytics.Metrics",
            code,
            400,
            message,
            "Correct the listing metrics request and preserve [from, to) range semantics.");

    private static AnalyticsCommandException CorruptStore(
        string message,
        string catalogKey,
        Guid listingId,
        DailyMetricsRangeRequest request,
        DateOnly actualDate) =>
        new(
            "Analytics.Persistence",
            "ANALYTICS_AGGREGATE_STORE_CORRUPT",
            500,
            message,
            "Stop metrics reads and repair the Analytics aggregate persistence invariant.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = catalogKey,
                ["listingId"] = listingId,
                ["fromInclusive"] = request.FromInclusive,
                ["toExclusive"] = request.ToExclusive,
                ["actualDate"] = actualDate,
            });
}
