namespace Aggregator.Promotion.Api;

internal static class PromotionAuthorizationPolicies
{
    public const string ManageListing = "promotion.manage-listing";
    public const string ManageCatalog = "promotion.manage-catalog";
    public const string Read = "promotion.read";
    public const string TestContracts = "promotion.test-contracts";
}

internal static class PromotionRateLimitPolicies
{
    public const string Commands = "promotion-commands";
    public const string Reads = "promotion-reads";
}
