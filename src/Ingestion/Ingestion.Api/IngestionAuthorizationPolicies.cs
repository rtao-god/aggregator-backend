namespace Aggregator.Ingestion.Api;

public static class IngestionAuthorizationPolicies
{
    public const string Submit = "IngestionSubmit";
    public const string Read = "IngestionRead";
    public const string Review = "IngestionReview";
    public const string Commit = "IngestionCommit";
    public const string DeliverCatalog = "IngestionDeliverCatalog";
    public const string ManageProducers = "IngestionManageProducers";
}

public static class IngestionScopes
{
    public const string Submit = "ingestion.submit";
    public const string Read = "ingestion.read";
    public const string Review = "ingestion.review";
    public const string Commit = "ingestion.commit";
    public const string DeliverCatalog = "ingestion.deliver-catalog";
    public const string ManageProducers = "ingestion.manage-producers";
}
