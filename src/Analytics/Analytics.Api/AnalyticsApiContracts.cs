namespace Aggregator.Analytics.Api;

public static class AnalyticsAuthorizationPolicies
{
    public const string ViewListing = "analytics.view-listing";

    public const string TestContracts = "analytics.test-contracts";
}

public static class AnalyticsRateLimitPolicies
{
    public const string AntiAbuseTokens = "analytics-anti-abuse-tokens";

    public const string InteractionEvents = "analytics-interaction-events";

    public const string Metrics = "analytics-metrics";
}

public static class AnalyticsOperationIds
{
    public const string IssueAntiAbuseToken = "IssueAnalyticsAntiAbuseToken";

    public const string SubmitInteractionEvent = "SubmitAnalyticsInteractionEvent";

    public const string ReadDailyListingMetrics = "ReadAnalyticsDailyListingMetrics";
}
