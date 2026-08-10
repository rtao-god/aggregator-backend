using Aggregator.Analytics.Contracts;

namespace Aggregator.Analytics.Application;

/// <summary>Maps the canonical owner-authorized listing metrics range to the daily transport contract.</summary>
public sealed class ReadDailyListingMetricsService(
    ReadListingMetricsRangeService rangeService)
{
    public async Task<IReadOnlyList<DailyListingMetricsResponse>> ReadAsync(
        Guid actorId,
        string catalogKey,
        Guid listingId,
        DailyMetricsRangeRequest request,
        CancellationToken cancellationToken)
    {
        var metrics = await rangeService.ReadAsync(
            actorId,
            catalogKey,
            listingId,
            request,
            cancellationToken);
        return metrics
            .Select(AnalyticsContractMapper.ToResponse)
            .ToArray();
    }
}
