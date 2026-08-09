using Aggregator.Catalog.Domain;

namespace Catalog.Domain.Tests;

public sealed class ListingDisputeTests
{
    private static readonly DateTimeOffset OpenedAtUtc =
        new(2026, 8, 9, 19, 0, 0, TimeSpan.Zero);
    private static readonly Guid DisputeId =
        Guid.Parse("0198ff20-0000-7000-8000-000000000001");
    private static readonly Guid ListingId =
        Guid.Parse("0198ff20-0000-7000-8000-000000000002");
    private static readonly Guid ActorId =
        Guid.Parse("0198ff20-0000-7000-8000-000000000003");

    [Fact]
    public void OpenDisputeBlocksPromotion()
    {
        var dispute = ListingDispute.Open(
            DisputeId,
            ListingId,
            "Provider disputes the published ownership facts.",
            ActorId,
            OpenedAtUtc);

        Assert.Equal(ListingDisputeState.Open, dispute.State);
        Assert.True(dispute.BlocksPromotion);
        Assert.Equal(1, dispute.AggregateRevision);
        Assert.Null(dispute.ResolutionReason);
        Assert.Equal(
            dispute.ToSnapshot(),
            ListingDispute.Restore(dispute.ToSnapshot()).ToSnapshot());
    }

    [Fact]
    public void ResolutionRetainsOpeningEvidenceAndStopsBlockingPromotion()
    {
        var dispute = ListingDispute.Open(
            DisputeId,
            ListingId,
            "Provider disputes the published ownership facts.",
            ActorId,
            OpenedAtUtc);
        var resolverId =
            Guid.Parse("0198ff20-0000-7000-8000-000000000004");
        var resolvedAtUtc = OpenedAtUtc.AddHours(1);

        dispute.Resolve(
            expectedAggregateRevision: 1,
            actorId: resolverId,
            resolutionReason: "Catalog evidence was reviewed and the dispute was resolved.",
            resolvedAtUtc: resolvedAtUtc);

        Assert.Equal(ListingDisputeState.Resolved, dispute.State);
        Assert.False(dispute.BlocksPromotion);
        Assert.Equal(2, dispute.AggregateRevision);
        Assert.Equal(resolverId, dispute.ResolvedByActorId);
        Assert.Equal(resolvedAtUtc, dispute.ResolvedAtUtc);
        Assert.Contains("ownership facts", dispute.OpenReason, StringComparison.Ordinal);
        Assert.Equal(
            dispute.ToSnapshot(),
            ListingDispute.Restore(dispute.ToSnapshot()).ToSnapshot());
    }

    [Fact]
    public void ResolvedDisputeCannotBeResolvedAgain()
    {
        var dispute = ListingDispute.Open(
            DisputeId,
            ListingId,
            "Provider disputes the published ownership facts.",
            ActorId,
            OpenedAtUtc);
        dispute.Resolve(
            expectedAggregateRevision: 1,
            actorId: ActorId,
            resolutionReason: "Resolved.",
            resolvedAtUtc: OpenedAtUtc.AddMinutes(1));

        Assert.Throws<CatalogInvariantException>(() =>
            dispute.Resolve(
                expectedAggregateRevision: 2,
                actorId: ActorId,
                resolutionReason: "Resolve again.",
                resolvedAtUtc: OpenedAtUtc.AddMinutes(2)));
    }

    [Fact]
    public void StaleRevisionIsExplicit()
    {
        var dispute = ListingDispute.Open(
            DisputeId,
            ListingId,
            "Provider disputes the published ownership facts.",
            ActorId,
            OpenedAtUtc);

        var exception = Assert.Throws<CatalogListingDisputeConcurrencyException>(() =>
            dispute.Resolve(
                expectedAggregateRevision: 2,
                actorId: ActorId,
                resolutionReason: "Resolve.",
                resolvedAtUtc: OpenedAtUtc.AddMinutes(1)));

        Assert.Equal(DisputeId, exception.DisputeId);
        Assert.Equal(2, exception.ExpectedRevision);
        Assert.Equal(1, exception.ActualRevision);
    }

    [Fact]
    public void PersistedLifecycleTupleMustBeComplete()
    {
        var exception = Assert.Throws<CatalogInvariantException>(() =>
            ListingDispute.Restore(new ListingDisputeSnapshot(
                DisputeId,
                ListingId,
                ListingDisputeState.Resolved,
                "Opened.",
                ActorId,
                OpenedAtUtc,
                ResolutionReason: null,
                ResolvedByActorId: null,
                ResolvedAtUtc: null,
                AggregateRevision: 2)));

        Assert.Contains("lifecycle fields", exception.Message, StringComparison.Ordinal);
    }
}
