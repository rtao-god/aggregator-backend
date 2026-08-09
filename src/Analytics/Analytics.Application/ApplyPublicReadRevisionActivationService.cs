using Aggregator.Analytics.Domain;
using Aggregator.Query.Contracts;

namespace Aggregator.Analytics.Application;

/// <summary>Maps and applies one producer-owned Query public-read activation.</summary>
public sealed class ApplyPublicReadRevisionActivationService(
    IPublicReadActivationProjectionStore store,
    TimeProvider timeProvider)
{
    public Task<PublicReadActivationProjectionResult> ApplyAsync(
        PublicReadRevisionActivated activation,
        string payloadDigest,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        try
        {
            var listingIds = RequireCanonicalListings(activation.PublicListingIds);
            var placementContracts = RequireCanonicalPlacements(activation.SponsoredPlacements);
            ValidateMembershipDigest(
                activation.MembershipDigest,
                listingIds,
                placementContracts);
            var placements = placementContracts
                .Select(MapPlacement)
                .ToArray();
            var projection = PublicReadReferenceProjection.Create(
                activation.PublicReadRevisionId,
                activation.CatalogKey,
                activation.BaseProjectionId,
                activation.PromotionOverlayId,
                activation.SafetyOverlayId,
                activation.SourcePublicationId,
                activation.PublicReadContentDigest,
                activation.MembershipDigest,
                activation.OccurredAtUtc,
                listingIds,
                placements);
            var inbox = PublicReadActivationInboxMessage.Create(
                activation.EventId,
                QueryIntegrationEventTypes.PublicReadRevisionActivated,
                QueryIntegrationEventContracts.PublicReadRevisionActivated,
                payloadDigest,
                activation.ActivationRevision,
                timeProvider.GetUtcNow(),
                correlationId);
            return store.ApplyAsync(projection, inbox, cancellationToken);
        }
        catch (AnalyticsDomainException exception)
        {
            throw InvalidActivation(exception);
        }
    }

    private static IReadOnlyList<Guid> RequireCanonicalListings(
        IReadOnlyList<Guid>? publicListingIds)
    {
        if (publicListingIds is null)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_MEMBERSHIP_REQUIRED",
                "Query public-read activation has no public listing membership.");
        }

        var listingIds = publicListingIds.ToArray();
        if (!listingIds.SequenceEqual(listingIds.Order()))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_MEMBERSHIP_ORDER_INVALID",
                "Query public-read activation listing membership is not in canonical identity order.");
        }

        return Array.AsReadOnly(listingIds);
    }

    private static IReadOnlyList<PublicReadSponsoredPlacementReference> RequireCanonicalPlacements(
        IReadOnlyList<PublicReadSponsoredPlacementReference>? sponsoredPlacements)
    {
        if (sponsoredPlacements is null)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_MEMBERSHIP_REQUIRED",
                "Query public-read activation has no sponsored placement membership contract.");
        }

        var placements = sponsoredPlacements.ToArray();
        if (placements.Any(placement => placement is null))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_REQUIRED",
                "Query public-read activation contains an empty sponsored placement reference.");
        }

        if (!placements.Select(placement => placement.PlacementId)
                .SequenceEqual(placements.Select(placement => placement.PlacementId).Order()))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_PLACEMENT_ORDER_INVALID",
                "Query public-read activation sponsored placements are not in canonical identity order.");
        }

        return Array.AsReadOnly(placements);
    }

    private static void ValidateMembershipDigest(
        string membershipDigest,
        IReadOnlyList<Guid> listingIds,
        IReadOnlyList<PublicReadSponsoredPlacementReference> placements)
    {
        var normalizedDigest = AnalyticsDomainRules.RequireDigest(
            membershipDigest,
            nameof(membershipDigest));
        var actualDigest = AnalyticsCanonicalJson.ComputeDigest(
            new PublicReadMembershipDigestDocument(listingIds, placements));
        if (!string.Equals(normalizedDigest, actualDigest, StringComparison.Ordinal))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PUBLIC_MEMBERSHIP_DIGEST_MISMATCH",
                "Query public-read activation membership does not match its producer digest.");
        }
    }

    private static PublicReadSponsoredPlacementProjection MapPlacement(
        PublicReadSponsoredPlacementReference placement) =>
        PublicReadSponsoredPlacementProjection.Create(
            placement.PlacementId,
            placement.ListingId,
            placement.ScopeType switch
            {
                PublicReadPlacementScopeTypeContract.Catalog =>
                    PublicReadSponsoredPlacementScope.Catalog,
                PublicReadPlacementScopeTypeContract.Category =>
                    PublicReadSponsoredPlacementScope.Category,
                PublicReadPlacementScopeTypeContract.District =>
                    PublicReadSponsoredPlacementScope.District,
                PublicReadPlacementScopeTypeContract.EditorialLanding =>
                    PublicReadSponsoredPlacementScope.EditorialLanding,
                _ => throw new AnalyticsDomainException(
                    "ANALYTICS_PUBLIC_PLACEMENT_SCOPE_INVALID",
                    $"Query public-read activation contains unsupported placement scope '{placement.ScopeType}'."),
            },
            placement.ScopeKey,
            placement.StartsAtUtc,
            placement.HardExpiryAtUtc);

    private static AnalyticsCommandException InvalidActivation(
        AnalyticsDomainException exception) =>
        new(
            "Analytics.PublicReference",
            exception.Code,
            422,
            exception.Message,
            "Correct or replay the exact Query public-read activation before accepting dependent interaction events.");

    private sealed record PublicReadMembershipDigestDocument(
        IReadOnlyList<Guid> PublicListingIds,
        IReadOnlyList<PublicReadSponsoredPlacementReference> SponsoredPlacements);
}
