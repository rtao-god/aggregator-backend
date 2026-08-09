using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

/// <summary>Builds the minimal producer-owned event for one exact public-read pointer switch.</summary>
public static class PublicReadActivationEventFactory
{
    public static PublicReadRevisionActivated Create(
        Guid eventId,
        PublicReadRevision revision,
        long activationRevision,
        IEnumerable<Guid> publicListingIds,
        IEnumerable<PublicReadSponsoredPlacementReference> sponsoredPlacements,
        DateTimeOffset occurredAtUtc)
    {
        if (eventId == Guid.Empty)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_EVENT_ID_INVALID",
                "Public-read activation event ID must be a non-empty UUID.");
        }

        ArgumentNullException.ThrowIfNull(revision);
        if (activationRevision <= 0)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_ACTIVATION_REVISION_INVALID",
                "Public-read activation revision must be positive.");
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_EVENT_TIME_NOT_UTC",
                "Public-read activation event timestamp must be normalized to UTC.");
        }

        ArgumentNullException.ThrowIfNull(publicListingIds);
        var listingIds = publicListingIds.Order().ToArray();
        if (listingIds.Any(listingId => listingId == Guid.Empty) ||
            listingIds.Distinct().Count() != listingIds.Length)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_MEMBERSHIP_INVALID",
                "Public-read activation membership must contain unique non-empty listing IDs.");
        }

        ArgumentNullException.ThrowIfNull(sponsoredPlacements);
        var placementCandidates = sponsoredPlacements.ToArray();
        if (placementCandidates.Any(placement => placement is null))
        {
            throw Failure(
                "QUERY_PUBLIC_READ_PLACEMENT_REFERENCE_INVALID",
                "Public-read activation cannot contain an empty sponsored placement reference.");
        }

        var placements = placementCandidates
            .OrderBy(placement => placement.PlacementId)
            .ToArray();
        if (placements.Any(placement =>
                placement.PlacementId == Guid.Empty ||
                placement.ListingId == Guid.Empty ||
                !Enum.IsDefined(placement.ScopeType) ||
                string.IsNullOrWhiteSpace(placement.ScopeKey) ||
                placement.ScopeKey.Length > 200 ||
                placement.StartsAtUtc.Offset != TimeSpan.Zero ||
                placement.HardExpiryAtUtc.Offset != TimeSpan.Zero ||
                placement.StartsAtUtc >= placement.HardExpiryAtUtc) ||
            placements.Select(placement => placement.PlacementId).Distinct().Count() != placements.Length)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_PLACEMENT_REFERENCE_INVALID",
                "Public-read activation contains an invalid or duplicate sponsored placement reference.");
        }

        var membership = listingIds.ToHashSet();
        var unknownPlacementListing = placements
            .FirstOrDefault(placement => !membership.Contains(placement.ListingId));
        if (unknownPlacementListing is not null)
        {
            throw Failure(
                "QUERY_PUBLIC_READ_PLACEMENT_LISTING_NOT_PUBLIC",
                $"Sponsored placement '{unknownPlacementListing.PlacementId}' references listing '{unknownPlacementListing.ListingId}' outside the public membership.");
        }

        var readonlyListings = Array.AsReadOnly(listingIds);
        var readonlyPlacements = Array.AsReadOnly(placements);
        var membershipDigest = QueryCanonicalJson.ComputeDigest(
            new PublicReadMembershipDigestDocument(readonlyListings, readonlyPlacements));
        return new PublicReadRevisionActivated(
            eventId,
            revision.Id,
            revision.CatalogKey,
            activationRevision,
            revision.BaseProjectionId,
            revision.PromotionOverlayId,
            revision.SafetyOverlayId,
            revision.SourcePublicationId,
            revision.ContentDigest,
            membershipDigest,
            readonlyListings,
            readonlyPlacements,
            occurredAtUtc);
    }

    private static QueryProjectionException Failure(string code, string detail) =>
        new(
            "Query.PublicReadActivation",
            code,
            500,
            detail,
            "Keep the previous public-read pointer visible and repair the Query activation producer before retrying.");

    private sealed record PublicReadMembershipDigestDocument(
        IReadOnlyList<Guid> PublicListingIds,
        IReadOnlyList<PublicReadSponsoredPlacementReference> SponsoredPlacements);
}
