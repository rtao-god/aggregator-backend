namespace Aggregator.Ingestion.Api;

public static class IngestionAuthorizationPolicies
{
    public const string Upload = "ingestion.upload";

    public const string Read = "ingestion.read";

    public const string TestContracts = "ingestion.test-contracts";
}

public static class IngestionRateLimitPolicies
{
    public const string BatchCommands = "ingestion-batch-commands";
}
