namespace Aggregator.Analytics.Domain;

public enum AggregateReadinessState
{
    Complete = 1,
    Partial = 2,
    Blocked = 3,
    Rebuilding = 4,
}

public sealed record InteractionCounts(
    long OrganicImpressions,
    long SponsoredImpressions,
    long ListingOpens,
    long WebsiteClicks,
    long PhoneClicks,
    long WhatsAppClicks,
    long EmailClicks,
    long MapClicks,
    long ExternalProfileClicks)
{
    public static InteractionCounts Create(
        long organicImpressions,
        long sponsoredImpressions,
        long listingOpens,
        long websiteClicks,
        long phoneClicks,
        long whatsAppClicks,
        long emailClicks,
        long mapClicks,
        long externalProfileClicks)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [nameof(organicImpressions)] = organicImpressions,
            [nameof(sponsoredImpressions)] = sponsoredImpressions,
            [nameof(listingOpens)] = listingOpens,
            [nameof(websiteClicks)] = websiteClicks,
            [nameof(phoneClicks)] = phoneClicks,
            [nameof(whatsAppClicks)] = whatsAppClicks,
            [nameof(emailClicks)] = emailClicks,
            [nameof(mapClicks)] = mapClicks,
            [nameof(externalProfileClicks)] = externalProfileClicks,
        };
        var invalid = values.FirstOrDefault(item => item.Value < 0);
        if (!string.IsNullOrEmpty(invalid.Key))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_METRIC_NEGATIVE",
                $"Metric '{invalid.Key}' cannot be negative.");
        }

        return new InteractionCounts(
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
}

public sealed class DailyListingMetrics
{
    private DailyListingMetrics(
        DateOnly date,
        string catalogKey,
        Guid listingId,
        string aggregationSourceDigest,
        int sourceReadRevisionCount,
        AggregateReadinessState readiness,
        InteractionCounts? counts,
        string? unavailableReason)
    {
        Date = date;
        CatalogKey = catalogKey;
        ListingId = listingId;
        AggregationSourceDigest = aggregationSourceDigest;
        SourceReadRevisionCount = sourceReadRevisionCount;
        Readiness = readiness;
        Counts = counts;
        UnavailableReason = unavailableReason;
    }

    public DateOnly Date { get; }

    public string CatalogKey { get; }

    public Guid ListingId { get; }

    public string AggregationSourceDigest { get; }

    public int SourceReadRevisionCount { get; }

    public AggregateReadinessState Readiness { get; }

    public InteractionCounts? Counts { get; }

    public string? UnavailableReason { get; }

    public static DailyListingMetrics Complete(
        DateOnly date,
        string catalogKey,
        Guid listingId,
        string aggregationSourceDigest,
        int sourceReadRevisionCount,
        InteractionCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return Create(
            date,
            catalogKey,
            listingId,
            aggregationSourceDigest,
            sourceReadRevisionCount,
            AggregateReadinessState.Complete,
            counts,
            unavailableReason: null);
    }

    public static DailyListingMetrics Unavailable(
        DateOnly date,
        string catalogKey,
        Guid listingId,
        string aggregationSourceDigest,
        int sourceReadRevisionCount,
        AggregateReadinessState readiness,
        string unavailableReason)
    {
        if (readiness == AggregateReadinessState.Complete)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_UNAVAILABLE_STATE_INVALID",
                "Complete metrics must carry observed counts.");
        }

        if (string.IsNullOrWhiteSpace(unavailableReason))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_UNAVAILABLE_REASON_REQUIRED",
                "Unavailable metrics require an explicit reason.");
        }

        return Create(
            date,
            catalogKey,
            listingId,
            aggregationSourceDigest,
            sourceReadRevisionCount,
            readiness,
            counts: null,
            unavailableReason.Trim());
    }

    private static DailyListingMetrics Create(
        DateOnly date,
        string catalogKey,
        Guid listingId,
        string aggregationSourceDigest,
        int sourceReadRevisionCount,
        AggregateReadinessState readiness,
        InteractionCounts? counts,
        string? unavailableReason)
    {
        var normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
        var normalizedDigest = AnalyticsDomainRules.RequireDigest(
            aggregationSourceDigest,
            nameof(aggregationSourceDigest));
        if (sourceReadRevisionCount < 0)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_SOURCE_REVISION_COUNT_INVALID",
                "Source read revision count cannot be negative.");
        }

        if (!Enum.IsDefined(readiness))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_READINESS_INVALID",
                $"Aggregate readiness '{readiness}' is unsupported.");
        }

        return new DailyListingMetrics(
            date,
            normalizedCatalogKey,
            listingId,
            normalizedDigest,
            sourceReadRevisionCount,
            readiness,
            counts,
            unavailableReason);
    }
}
