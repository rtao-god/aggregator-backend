namespace Aggregator.Analytics.Api;

/// <summary>Declares the OAuth scopes enforced by the Analytics transport owner.</summary>
public static class AnalyticsAuthorizationPolicies
{
    public const string ViewListing = "analytics.view-listing";

    public const string TestContracts = "analytics.test-contracts";
}

/// <summary>Declares independent request budgets for public intake and protected reads.</summary>
public static class AnalyticsRateLimitPolicies
{
    public const string AntiAbuseTokens = "analytics-anti-abuse-tokens";

    public const string InteractionEvents = "analytics-interaction-events";

    public const string Metrics = "analytics-metrics";
}

/// <summary>Provides stable operation identities for generated Analytics clients.</summary>
public static class AnalyticsOperationIds
{
    public const string IssueAntiAbuseToken = "IssueAnalyticsAntiAbuseToken";

    public const string SubmitInteractionEvent = "SubmitAnalyticsInteractionEvent";

    public const string ReadDailyListingMetrics = "ReadAnalyticsDailyListingMetrics";
}
