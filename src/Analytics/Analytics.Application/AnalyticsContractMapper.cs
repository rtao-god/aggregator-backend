using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

internal static class AnalyticsContractMapper
{
    public static InteractionEventKind ToDomain(InteractionEventKindContract value) => value switch
    {
        InteractionEventKindContract.SearchResultsViewed => InteractionEventKind.SearchResultsViewed,
        InteractionEventKindContract.ListingImpression => InteractionEventKind.ListingImpression,
        InteractionEventKindContract.ListingOpened => InteractionEventKind.ListingOpened,
        InteractionEventKindContract.WebsiteClicked => InteractionEventKind.WebsiteClicked,
        InteractionEventKindContract.PhoneClicked => InteractionEventKind.PhoneClicked,
        InteractionEventKindContract.WhatsAppClicked => InteractionEventKind.WhatsAppClicked,
        InteractionEventKindContract.EmailClicked => InteractionEventKind.EmailClicked,
        InteractionEventKindContract.MapClicked => InteractionEventKind.MapClicked,
        InteractionEventKindContract.ExternalProfileClicked => InteractionEventKind.ExternalProfileClicked,
        InteractionEventKindContract.ClaimStarted => InteractionEventKind.ClaimStarted,
        InteractionEventKindContract.ClaimSubmitted => InteractionEventKind.ClaimSubmitted,
        _ => throw UnsupportedContractEnum(nameof(InteractionEventKindContract), value),
    };

    public static ReferrerClass ToDomain(ReferrerClassContract value) => value switch
    {
        ReferrerClassContract.Direct => ReferrerClass.Direct,
        ReferrerClassContract.Internal => ReferrerClass.Internal,
        ReferrerClassContract.Search => ReferrerClass.Search,
        ReferrerClassContract.Social => ReferrerClass.Social,
        ReferrerClassContract.Campaign => ReferrerClass.Campaign,
        ReferrerClassContract.Other => ReferrerClass.Other,
        ReferrerClassContract.Unknown => ReferrerClass.Unknown,
        _ => throw UnsupportedContractEnum(nameof(ReferrerClassContract), value),
    };

    public static ConsentMode ToDomain(ConsentModeContract value) => value switch
    {
        ConsentModeContract.EssentialOnly => ConsentMode.EssentialOnly,
        ConsentModeContract.AnalyticsAllowed => ConsentMode.AnalyticsAllowed,
        _ => throw UnsupportedContractEnum(nameof(ConsentModeContract), value),
    };

    public static PlacementContext ToDomain(PlacementContextContract value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var exposureKind = value.ExposureKind switch
        {
            PlacementExposureKindContract.Organic => PlacementExposureKind.Organic,
            PlacementExposureKindContract.Sponsored => PlacementExposureKind.Sponsored,
            PlacementExposureKindContract.NotApplicable => PlacementExposureKind.NotApplicable,
            _ => throw UnsupportedContractEnum(nameof(PlacementExposureKindContract), value.ExposureKind),
        };
        return PlacementContext.Create(exposureKind, value.PlacementId, value.ScopeKey);
    }

    public static InteractionEventResponse ToResponse(
        PersistedInteractionEventReceipt receipt,
        InteractionAcceptanceStateContract acceptanceState)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        return new InteractionEventResponse(
            receipt.EventId,
            receipt.SemanticKey.ClientEventId,
            ToContract(receipt.SemanticKey.Kind),
            acceptanceState,
            ToContract(receipt.QualityState),
            receipt.ReceivedAtUtc,
            receipt.PublicReadRevisionId,
            receipt.ListingId);
    }


    public static AnalyticsAggregateRunResponse ToResponse(AnalyticsAggregateRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new AnalyticsAggregateRunResponse(
            run.RunId,
            run.FromInclusive,
            run.ToExclusive,
            run.State switch
            {
                AnalyticsAggregateRunState.Rebuilding => AnalyticsAggregateRunStateContract.Rebuilding,
                AnalyticsAggregateRunState.Complete => AnalyticsAggregateRunStateContract.Complete,
                AnalyticsAggregateRunState.Blocked => AnalyticsAggregateRunStateContract.Blocked,
                _ => throw UnsupportedDomainEnum(nameof(AnalyticsAggregateRunState), run.State),
            },
            run.StartedAtUtc,
            run.CompletedAtUtc,
            run.SourceDigest,
            run.MaterializedMetricCount,
            run.RemovedStaleMetricCount,
            run.MaterializedDayCount,
            run.FailureCode,
            run.FailureDetail,
            run.RequiredAction);
    }

    public static DailyListingMetricsResponse ToResponse(DailyListingMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        return new DailyListingMetricsResponse(
            metrics.Date,
            metrics.CatalogKey,
            metrics.ListingId,
            metrics.AggregationSourceDigest,
            metrics.SourceReadRevisionCount,
            ToContract(metrics.Readiness),
            metrics.Counts is null
                ? null
                : new InteractionCountsContract(
                    metrics.Counts.OrganicImpressions,
                    metrics.Counts.SponsoredImpressions,
                    metrics.Counts.ListingOpens,
                    metrics.Counts.WebsiteClicks,
                    metrics.Counts.PhoneClicks,
                    metrics.Counts.WhatsAppClicks,
                    metrics.Counts.EmailClicks,
                    metrics.Counts.MapClicks,
                    metrics.Counts.ExternalProfileClicks),
            metrics.UnavailableReason);
    }

    public static TrafficQualityStateContract ToContract(TrafficQualityState value) => value switch
    {
        TrafficQualityState.Accepted => TrafficQualityStateContract.Accepted,
        TrafficQualityState.SuspectedBot => TrafficQualityStateContract.SuspectedBot,
        TrafficQualityState.KnownBot => TrafficQualityStateContract.KnownBot,
        TrafficQualityState.RateLimited => TrafficQualityStateContract.RateLimited,
        TrafficQualityState.Invalid => TrafficQualityStateContract.Invalid,
        TrafficQualityState.Duplicate => TrafficQualityStateContract.Duplicate,
        _ => throw UnsupportedDomainEnum(nameof(TrafficQualityState), value),
    };

    public static InteractionEventKindContract ToContract(InteractionEventKind value) => value switch
    {
        InteractionEventKind.SearchResultsViewed => InteractionEventKindContract.SearchResultsViewed,
        InteractionEventKind.ListingImpression => InteractionEventKindContract.ListingImpression,
        InteractionEventKind.ListingOpened => InteractionEventKindContract.ListingOpened,
        InteractionEventKind.WebsiteClicked => InteractionEventKindContract.WebsiteClicked,
        InteractionEventKind.PhoneClicked => InteractionEventKindContract.PhoneClicked,
        InteractionEventKind.WhatsAppClicked => InteractionEventKindContract.WhatsAppClicked,
        InteractionEventKind.EmailClicked => InteractionEventKindContract.EmailClicked,
        InteractionEventKind.MapClicked => InteractionEventKindContract.MapClicked,
        InteractionEventKind.ExternalProfileClicked => InteractionEventKindContract.ExternalProfileClicked,
        InteractionEventKind.ClaimStarted => InteractionEventKindContract.ClaimStarted,
        InteractionEventKind.ClaimSubmitted => InteractionEventKindContract.ClaimSubmitted,
        _ => throw UnsupportedDomainEnum(nameof(InteractionEventKind), value),
    };

    public static AggregateReadinessStateContract ToContract(AggregateReadinessState value) => value switch
    {
        AggregateReadinessState.Complete => AggregateReadinessStateContract.Complete,
        AggregateReadinessState.Partial => AggregateReadinessStateContract.Partial,
        AggregateReadinessState.Blocked => AggregateReadinessStateContract.Blocked,
        AggregateReadinessState.Rebuilding => AggregateReadinessStateContract.Rebuilding,
        _ => throw UnsupportedDomainEnum(nameof(AggregateReadinessState), value),
    };

    private static AnalyticsCommandException UnsupportedContractEnum<TEnum>(
        string enumName,
        TEnum value)
        where TEnum : struct, Enum =>
        new(
            "Analytics.Contracts",
            "ANALYTICS_CONTRACT_ENUM_UNSUPPORTED",
            400,
            $"Contract enum '{enumName}' contains unsupported value '{value}'.",
            "Use one of the documented string enum values.");

    private static AnalyticsCommandException UnsupportedDomainEnum<TEnum>(
        string enumName,
        TEnum value)
        where TEnum : struct, Enum =>
        new(
            "Analytics.Contracts",
            "ANALYTICS_DOMAIN_ENUM_UNSUPPORTED",
            500,
            $"Domain enum '{enumName}' contains unsupported value '{value}'.",
            "Stop the response and align the Analytics domain-to-contract mapping.");
}
