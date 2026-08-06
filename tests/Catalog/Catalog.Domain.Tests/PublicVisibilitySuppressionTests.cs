using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class PublicVisibilitySuppressionTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 6, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ListingSuppressionUsesExactIdentityAndRevisionedLifecycle()
    {
        var listingId = Guid.Parse("0198f900-0000-7000-8000-000000000001");
        var suppressionId = Guid.Parse("0198f900-0000-7000-8000-000000000002");
        var actorId = Guid.Parse("0198f900-0000-7000-8000-000000000003");
        var target = PublicVisibilitySuppressionTarget.Create(
            PublicVisibilitySuppressionTargetKind.Listing,
            listingId,
            listingId.ToString("D"));

        var requested = PublicVisibilitySuppression.Request(
            suppressionId,
            CatalogKey.Create("berlin-recording-services"),
            target,
            "legal-removal",
            "catalog/claims/private/evidence-001",
            PublicVisibilitySuppressionResponseMode.Gone,
            Timestamp,
            Timestamp.AddDays(7),
            actorId,
            "Remove the listing while the replacement publication is prepared.",
            Timestamp);
        var active = requested.Activate(
            expectedRevision: 1,
            actorId,
            "Safety block accepted.",
            Timestamp);
        var resolved = active.Resolve(
            expectedRevision: 2,
            actorId,
            "Replacement publication no longer contains the listing.",
            Timestamp.AddHours(1));

        Assert.Equal(PublicVisibilitySuppressionState.Requested, requested.State);
        Assert.Equal(1, requested.Revision);
        Assert.Equal(PublicVisibilitySuppressionState.Active, active.State);
        Assert.Equal(2, active.Revision);
        Assert.Equal(PublicVisibilitySuppressionState.Resolved, resolved.State);
        Assert.Equal(3, resolved.Revision);
        Assert.Equal("catalog/claims/private/evidence-001", resolved.PrivateEvidenceReference);
        Assert.Equal(listingId, resolved.Target.ListingId);
        Assert.Equal(listingId.ToString("D"), resolved.Target.TargetKey);
    }

    [Fact]
    public void StaleSuppressionRevisionIsRejected()
    {
        var listingId = Guid.Parse("0198f900-0000-7000-8000-000000000010");
        var requested = PublicVisibilitySuppression.Request(
            Guid.Parse("0198f900-0000-7000-8000-000000000011"),
            CatalogKey.Create("berlin-recording-services"),
            PublicVisibilitySuppressionTarget.Create(
                PublicVisibilitySuppressionTargetKind.Listing,
                listingId,
                listingId.ToString("D")),
            "privacy-request",
            "catalog/privacy/request-010",
            PublicVisibilitySuppressionResponseMode.HideAsNotFound,
            Timestamp,
            expiresAtUtc: null,
            Guid.Parse("0198f900-0000-7000-8000-000000000012"),
            "Hide the exact listing.",
            Timestamp);

        var exception = Assert.Throws<CatalogSuppressionConcurrencyException>(() =>
            requested.Activate(
                expectedRevision: 2,
                Guid.Parse("0198f900-0000-7000-8000-000000000012"),
                "Invalid stale command.",
                Timestamp));

        Assert.Equal(2, exception.ExpectedRevision);
        Assert.Equal(1, exception.ActualRevision);
    }

    [Fact]
    public void ChildSuppressionCannotCarryListingScopeOrListingResponseMode()
    {
        var listingId = Guid.Parse("0198f900-0000-7000-8000-000000000020");
        var mediaId = Guid.Parse("0198f900-0000-7000-8000-000000000021");

        Assert.Throws<ArgumentException>(() => PublicVisibilitySuppressionTarget.Create(
            PublicVisibilitySuppressionTargetKind.Media,
            listingId,
            mediaId.ToString("D")));

        var target = PublicVisibilitySuppressionTarget.Create(
            PublicVisibilitySuppressionTargetKind.Media,
            listingId: null,
            mediaId.ToString("D"));
        Assert.Throws<CatalogInvariantException>(() => PublicVisibilitySuppression.Request(
            Guid.Parse("0198f900-0000-7000-8000-000000000022"),
            CatalogKey.Create("berlin-recording-services"),
            target,
            "rights-revoked",
            "catalog/media/revocation-021",
            PublicVisibilitySuppressionResponseMode.Gone,
            Timestamp,
            expiresAtUtc: null,
            Guid.Parse("0198f900-0000-7000-8000-000000000023"),
            "Remove only the revoked media asset.",
            Timestamp));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/catalog/../admin")]
    [InlineData("/catalog/listing?preview=true")]
    [InlineData("/catalog/listing#fragment")]
    public void RouteSuppressionRequiresNormalizedAbsolutePath(string targetKey)
    {
        Assert.Throws<ArgumentException>(() => PublicVisibilitySuppressionTarget.Create(
            PublicVisibilitySuppressionTargetKind.Route,
            listingId: null,
            targetKey));
    }
    [Theory]
    [InlineData(PublicVisibilitySuppressionState.Active, 3L)]
    [InlineData(PublicVisibilitySuppressionState.Resolved, 2L)]
    [InlineData(PublicVisibilitySuppressionState.Resolved, 4L)]
    public void RestoreRejectsNonCanonicalStateRevisionPair(
        PublicVisibilitySuppressionState state,
        long revision)
    {
        var listingId = Guid.Parse("0198f900-0000-7000-8000-000000000030");

        Assert.Throws<CatalogInvariantException>(() => PublicVisibilitySuppression.Restore(
            Guid.Parse("0198f900-0000-7000-8000-000000000031"),
            CatalogKey.Create("berlin-recording-services"),
            PublicVisibilitySuppressionTarget.Create(
                PublicVisibilitySuppressionTargetKind.Listing,
                listingId,
                listingId.ToString("D")),
            "legal-removal",
            "catalog/claims/private/evidence-030",
            PublicVisibilitySuppressionResponseMode.Gone,
            Timestamp,
            expiresAtUtc: null,
            state,
            revision,
            Guid.Parse("0198f900-0000-7000-8000-000000000032"),
            "Persisted transition.",
            Timestamp));
    }

}
