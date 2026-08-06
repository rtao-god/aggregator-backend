namespace Aggregator.Catalog.Api;

public static class CatalogMediaAuthorizationPolicies
{
    public const string Manage = "catalog.media.manage";
    public const string Read = "catalog.media.read";
    public const string RevokeRights = "catalog.media.revoke-rights";
}

internal static class CatalogMediaRateLimitPolicies
{
    public const string Commands = "catalog-media-commands";
    public const string Reads = "catalog-media-reads";
}

public static class CatalogMediaOperationIds
{
    public const string Register = "RegisterCatalogMedia";
    public const string PrepareUpload = "PrepareCatalogMediaUpload";
    public const string CompleteUpload = "CompleteCatalogMediaUpload";
    public const string RevokeRights = "RevokeCatalogMediaRights";
    public const string Get = "GetCatalogMedia";
}
