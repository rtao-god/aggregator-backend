namespace Aggregator.Promotion.Api;

public static class PromotionOperationIds
{
    public const string CreateProduct = "CreatePromotionProduct";
    public const string AddProductRevision = "AddPromotionProductRevision";
    public const string ChangeProductState = "ChangePromotionProductState";
    public const string GetProduct = "GetPromotionProduct";

    public const string GrantEntitlement = "GrantPromotionEntitlement";
    public const string PauseEntitlement = "PausePromotionEntitlement";
    public const string ResumeEntitlement = "ResumePromotionEntitlement";
    public const string RevokeEntitlement = "RevokePromotionEntitlement";
    public const string GetEntitlement = "GetPromotionEntitlement";
    public const string ListListingEntitlements = "ListListingPromotionEntitlements";

    public const string CreatePlacement = "CreateSponsoredPlacement";
    public const string AddPlacementRevision = "AddSponsoredPlacementRevision";
    public const string PausePlacement = "PauseSponsoredPlacement";
    public const string ResumePlacement = "ResumeSponsoredPlacement";
    public const string RevokePlacement = "RevokeSponsoredPlacement";
    public const string GetPlacement = "GetSponsoredPlacement";
    public const string GetPlacementCalendar = "GetPromotionPlacementCalendar";
}
