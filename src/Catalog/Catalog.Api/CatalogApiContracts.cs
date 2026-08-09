namespace Aggregator.Catalog.Api;

public static class CatalogAuthorizationPolicies
{
    public const string EditConfiguration = "catalog.edit-configuration";
    public const string ActivateConfiguration = "catalog.activate-configuration";
    public const string EditListing = "catalog.edit-listing";
    public const string Review = "catalog.review";
    public const string Publish = "catalog.publish";
    public const string ManageClaims = "catalog.manage-claims";
    public const string ManageVisibility = "catalog.manage-visibility";
    public const string Ingestion = "catalog.ingestion";
}

public static class CatalogOperationIds
{
    public const string ImportConfiguration = "ImportCatalogConfiguration";
    public const string ActivateConfiguration = "ActivateCatalogConfiguration";
    public const string CreateListing = "CreateCatalogListing";
    public const string CreateListingRevision = "CreateCatalogListingRevision";
    public const string ApproveListingRevision = "ApproveCatalogListingRevision";
    public const string RejectListingRevision = "RejectCatalogListingRevision";
    public const string ArchiveListing = "ArchiveCatalogListing";
    public const string OpenListingDispute = "OpenCatalogListingDispute";
    public const string ResolveListingDispute = "ResolveCatalogListingDispute";
    public const string CreatePublication = "CreateCatalogPublication";
    public const string GetOperation = "GetCatalogOperation";
    public const string RollbackPublication = "RollbackCatalogPublication";
    public const string CreateVisibilitySuppression = "CreateCatalogVisibilitySuppression";
    public const string ResolveVisibilitySuppression = "ResolveCatalogVisibilitySuppression";
    public const string CreateClaim = "CreateCatalogClaim";
    public const string VerifyClaim = "VerifyCatalogClaim";
    public const string RejectClaim = "RejectCatalogClaim";
    public const string RevokeClaim = "RevokeCatalogClaim";
    public const string UpsertDraftFromIngestion = "UpsertCatalogDraftFromIngestion";
}

public static class CatalogRateLimitPolicies
{
    public const string Command = "catalog-command";
    public const string Ingestion = "catalog-ingestion";
}
