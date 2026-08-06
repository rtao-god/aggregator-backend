using Aggregator.Query.Domain;

namespace Query.Domain.Tests;

public sealed class QueryVisibilitySuppressionTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 6, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ActiveListingSuppressionUsesExactIdentityAndHalfOpenExpiry()
    {
        var listingId = Guid.Parse("0198fa00-0000-7000-8000-000000000001");
        var suppression = QueryVisibilitySuppression.Create(
            Guid.Parse("0198fa00-0000-7000-8000-000000000002"),
            "berlin-recording-services",
            QueryVisibilitySuppressionTargetKind.Listing,
            listingId,
            listingId.ToString("D"),
            "legal-removal",
            QueryVisibilitySuppressionResponseMode.Gone,
            QueryVisibilitySuppressionState.Active,
            Timestamp,
            Timestamp.AddHours(1),
            aggregateRevision: 2,
            Timestamp);

        Assert.True(suppression.IsMaterialized);
        Assert.True(suppression.IsEffectiveAt(Timestamp));
        Assert.True(suppression.IsEffectiveAt(Timestamp.AddMinutes(59)));
        Assert.False(suppression.IsEffectiveAt(Timestamp.AddHours(1)));
    }

    [Fact]
    public void ResolvedSuppressionRemainsProjectedStateButIsNotMaterialized()
    {
        var listingId = Guid.Parse("0198fa00-0000-7000-8000-000000000010");
        var suppression = QueryVisibilitySuppression.Create(
            Guid.Parse("0198fa00-0000-7000-8000-000000000011"),
            "berlin-recording-services",
            QueryVisibilitySuppressionTargetKind.Listing,
            listingId,
            listingId.ToString("D"),
            "privacy-request",
            QueryVisibilitySuppressionResponseMode.HideAsNotFound,
            QueryVisibilitySuppressionState.Resolved,
            Timestamp,
            expiresAtUtc: null,
            aggregateRevision: 3,
            Timestamp.AddMinutes(10));

        Assert.False(suppression.IsMaterialized);
        Assert.False(suppression.IsEffectiveAt(Timestamp.AddMinutes(10)));
    }

    [Fact]
    public void ChildSuppressionRequiresGlobalIdentityAndChildResponseMode()
    {
        var listingId = Guid.Parse("0198fa00-0000-7000-8000-000000000020");
        var contactId = Guid.Parse("0198fa00-0000-7000-8000-000000000021");

        var scopeException = Assert.Throws<QueryDomainException>(() => QueryVisibilitySuppression.Create(
            Guid.Parse("0198fa00-0000-7000-8000-000000000022"),
            "berlin-recording-services",
            QueryVisibilitySuppressionTargetKind.Contact,
            listingId,
            contactId.ToString("D"),
            "privacy-request",
            QueryVisibilitySuppressionResponseMode.OmitChildElement,
            QueryVisibilitySuppressionState.Active,
            Timestamp,
            expiresAtUtc: null,
            aggregateRevision: 2,
            Timestamp));
        Assert.Equal("QUERY_VISIBILITY_NON_LISTING_SCOPE_INVALID", scopeException.Code);

        var modeException = Assert.Throws<QueryDomainException>(() => QueryVisibilitySuppression.Create(
            Guid.Parse("0198fa00-0000-7000-8000-000000000023"),
            "berlin-recording-services",
            QueryVisibilitySuppressionTargetKind.Contact,
            listingId: null,
            contactId.ToString("D"),
            "privacy-request",
            QueryVisibilitySuppressionResponseMode.Gone,
            QueryVisibilitySuppressionState.Active,
            Timestamp,
            expiresAtUtc: null,
            aggregateRevision: 2,
            Timestamp));
        Assert.Equal("QUERY_VISIBILITY_RESPONSE_MODE_MISMATCH", modeException.Code);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("/catalog/../admin")]
    [InlineData("/catalog/listing?preview=true")]
    [InlineData("/catalog/listing#fragment")]
    public void RouteSuppressionRejectsNonCanonicalTarget(string targetKey)
    {
        var exception = Assert.Throws<QueryDomainException>(() => QueryVisibilitySuppression.Create(
            Guid.Parse("0198fa00-0000-7000-8000-000000000030"),
            "berlin-recording-services",
            QueryVisibilitySuppressionTargetKind.Route,
            listingId: null,
            targetKey,
            "legal-removal",
            QueryVisibilitySuppressionResponseMode.Gone,
            QueryVisibilitySuppressionState.Active,
            Timestamp,
            expiresAtUtc: null,
            aggregateRevision: 2,
            Timestamp));

        Assert.Equal("QUERY_VISIBILITY_ROUTE_TARGET_INVALID", exception.Code);
    }

    [Fact]
    public void ActiveSuppressionAdvancesOnlyToExactResolvedSuccessor()
    {
        var current = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 2,
            Timestamp);
        var resolved = CreateListingSuppression(
            QueryVisibilitySuppressionState.Resolved,
            aggregateRevision: 3,
            Timestamp.AddMinutes(1));

        current.EnsureCanAdvanceTo(resolved);
    }

    [Fact]
    public void InitialProjectionRequiresActiveRevisionTwo()
    {
        var valid = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 2,
            Timestamp);
        valid.EnsureValidInitialProjection();

        var resolvedFirst = CreateListingSuppression(
            QueryVisibilitySuppressionState.Resolved,
            aggregateRevision: 3,
            Timestamp.AddMinutes(1));
        var resolvedException = Assert.Throws<QueryDomainException>(() =>
            resolvedFirst.EnsureValidInitialProjection());
        Assert.Equal("QUERY_VISIBILITY_INITIAL_REVISION_INVALID", resolvedException.Code);

        var skippedActive = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 3,
            Timestamp.AddMinutes(1));
        var revisionException = Assert.Throws<QueryDomainException>(() =>
            skippedActive.EnsureValidInitialProjection());
        Assert.Equal("QUERY_VISIBILITY_INITIAL_REVISION_INVALID", revisionException.Code);
    }

    [Fact]
    public void SuppressionRevisionCannotChangeImmutableIdentity()
    {
        var current = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 2,
            Timestamp);
        var changedReason = CreateListingSuppression(
            QueryVisibilitySuppressionState.Resolved,
            aggregateRevision: 3,
            Timestamp.AddMinutes(1),
            publicReasonClass: "privacy-request");

        var exception = Assert.Throws<QueryDomainException>(() =>
            current.EnsureCanAdvanceTo(changedReason));

        Assert.Equal("QUERY_VISIBILITY_IDENTITY_CHANGED", exception.Code);
    }

    [Fact]
    public void SuppressionRevisionGapIsRejected()
    {
        var current = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 2,
            Timestamp);
        var skippedRevision = CreateListingSuppression(
            QueryVisibilitySuppressionState.Resolved,
            aggregateRevision: 4,
            Timestamp.AddMinutes(1));

        var exception = Assert.Throws<QueryDomainException>(() =>
            current.EnsureCanAdvanceTo(skippedRevision));

        Assert.Equal("QUERY_VISIBILITY_REVISION_GAP", exception.Code);
    }

    [Fact]
    public void SuppressionCannotPublishAnotherActiveRevision()
    {
        var current = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 2,
            Timestamp);
        var activeSuccessor = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 3,
            Timestamp.AddMinutes(1));

        var exception = Assert.Throws<QueryDomainException>(() =>
            current.EnsureCanAdvanceTo(activeSuccessor));

        Assert.Equal("QUERY_VISIBILITY_TRANSITION_INVALID", exception.Code);
    }

    [Fact]
    public void SuppressionRevisionTimeCannotRegress()
    {
        var current = CreateListingSuppression(
            QueryVisibilitySuppressionState.Active,
            aggregateRevision: 2,
            Timestamp.AddMinutes(2));
        var resolved = CreateListingSuppression(
            QueryVisibilitySuppressionState.Resolved,
            aggregateRevision: 3,
            Timestamp.AddMinutes(1));

        var exception = Assert.Throws<QueryDomainException>(() =>
            current.EnsureCanAdvanceTo(resolved));

        Assert.Equal("QUERY_VISIBILITY_EVENT_TIME_REGRESSION", exception.Code);
    }

    private static QueryVisibilitySuppression CreateListingSuppression(
        QueryVisibilitySuppressionState state,
        long aggregateRevision,
        DateTimeOffset occurredAtUtc,
        string publicReasonClass = "legal-removal")
    {
        var listingId = Guid.Parse("0198fa00-0000-7000-8000-000000000040");
        return QueryVisibilitySuppression.Create(
            Guid.Parse("0198fa00-0000-7000-8000-000000000041"),
            "berlin-recording-services",
            QueryVisibilitySuppressionTargetKind.Listing,
            listingId,
            listingId.ToString("D"),
            publicReasonClass,
            QueryVisibilitySuppressionResponseMode.Gone,
            state,
            Timestamp,
            expiresAtUtc: null,
            aggregateRevision,
            occurredAtUtc);
    }
}
