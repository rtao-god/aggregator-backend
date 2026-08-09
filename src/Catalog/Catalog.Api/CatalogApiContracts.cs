namespace Aggregator.Catalog.Api;

public static class CatalogAuthorizationPolicies
{
    public const string ManageConfiguration = "catalog.manage-config";
    public const string EditListing = "catalog.edit-listing";
    public const string Publish = "catalog.publish";
    public const string Rollback = "catalog.rollback";
    public const string ManageVisibility = "catalog.manage-visibility";
    public const string SubmitClaim = "catalog.submit-claim";
    public const string VerifyClaim = "catalog.verify-claim";
    public const string TestContracts = "catalog.test-contracts";
}

public static class CatalogOperationIds
{
    public const string ImportConfiguration = "ImportCatalogConfiguration";
    public const string ActivateConfiguration = "ActivateCatalogConfiguration";
    public const string CreateListing = "CreateCatalogListing";
    public const string GetListing = "GetCatalogListing";
    public const string CreateListingRevision = "CreateCatalogListingRevision";
    public const string ApproveListingRevision = "ApproveCatalogListingRevision";
    public const string RejectListingRevision = "RejectCatalogListingRevision";
    public const string ArchiveListing = "ArchiveCatalogListing";
    public const string CreatePublication = "CreateCatalogPublication";
    public const string GetOperation = "GetCatalogOperation";
    public const string RollbackPublication = "RollbackCatalogPublication";
    public const string CreateVisibilitySuppression = "CreateCatalogVisibilitySuppression";
    public const string ResolveVisibilitySuppression = "ResolveCatalogVisibilitySuppression";
    public const string SubmitClaim = "SubmitCatalogListingClaim";
    public const string VerifyClaim = "VerifyCatalogListingClaim";
    public const string RejectClaim = "RejectCatalogListingClaim";
    public const string RevokeClaim = "RevokeCatalogListingClaim";
}

public static class CatalogRateLimitPolicies
{
    public const string Command = "catalog-command";
}
