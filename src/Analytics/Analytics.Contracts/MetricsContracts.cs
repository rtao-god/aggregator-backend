namespace Aggregator.Analytics.Contracts;

public enum AggregateReadinessStateContract
{
    Complete = 1,
    Partial = 2,
    Blocked = 3,
    Rebuilding = 4,
}

public sealed record InteractionCountsContract(
    long OrganicImpressions,
    long SponsoredImpressions,
    long ListingOpens,
    long WebsiteClicks,
    long PhoneClicks,
    long WhatsAppClicks,
    long EmailClicks,
    long MapClicks,
    long ExternalProfileClicks);

public sealed record DailyListingMetricsResponse(
    DateOnly Date,
    string CatalogKey,
    Guid ListingId,
    string AggregationSourceDigest,
    int SourceReadRevisionCount,
    AggregateReadinessStateContract Readiness,
    InteractionCountsContract? Counts,
    string? UnavailableReason);

public sealed record DailyMetricsRangeRequest(DateOnly FromInclusive, DateOnly ToExclusive)
{
    public void Validate()
    {
        if (ToExclusive <= FromInclusive)
        {
            throw new ArgumentException("Metrics range must be non-empty and use [from, to) semantics.");
        }

        if (ToExclusive.DayNumber - FromInclusive.DayNumber > 366)
        {
            throw new ArgumentException("Metrics range cannot exceed 366 days.");
        }
    }
}
