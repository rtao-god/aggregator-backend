using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogListingAccessGrantEventTests
{
    private static readonly DateTimeOffset GrantedAtUtc =
        new(2026, 8, 9, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid ClaimId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000001");
    private static readonly Guid ListingId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000002");
    private static readonly Guid OwnerActorId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000003");
    private static readonly Guid ReviewerActorId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000004");
    private static readonly Guid GrantId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000005");
    private static readonly Guid EventId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000006");
    private static readonly Guid CausationId =
        Guid.Parse("0198ff10-0000-7000-8000-000000000007");

    [Fact]
    public void ActiveGrantEventCarriesEveryPermissionAndNoPrivateClaimEvidence()
    {
        var grant = CreateGrant();

        var message = CatalogListingAccessGrantEventFactory.Create(
            grant,
            EventId,
            GrantedAtUtc,
            CatalogEventContext.Create("catalog-access-test", CausationId));
        var integrationEvent = Deserialize(message.Payload);

        Assert.Equal(CatalogIntegrationEventTypes.ListingAccessGrantChanged, message.EventType);
        Assert.Equal(CatalogIntegrationEventContracts.ListingAccessGrantChanged, message.ContractIdentity);
        Assert.Equal("catalog-access-test", message.CorrelationId);
        Assert.Equal(CausationId, message.CausationId);
        Assert.Matches("^[0-9a-f]{64}$", message.PayloadDigest);
        Assert.Equal(EventId, integrationEvent.EventId);
        Assert.Equal(GrantId, integrationEvent.GrantId);
        Assert.Equal(ListingId, integrationEvent.ListingId);
        Assert.Equal(OwnerActorId, integrationEvent.ActorId);
        Assert.Equal(CatalogListingAccessGrantStateContract.Active, integrationEvent.State);
        Assert.Equal(1, integrationEvent.AggregateRevision);
        Assert.Equal(
            Enum.GetValues<ListingAccessScopeContract>(),
            integrationEvent.Permissions);
        Assert.DoesNotContain("evidenceReference", message.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("evidenceDigest", message.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("claimId", message.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public void RevocationCreatesTheNextExactGrantRevision()
    {
        var grant = CreateGrant();
        var revokedAtUtc = GrantedAtUtc.AddHours(1);
        grant.Revoke(ReviewerActorId, "owner access revoked", revokedAtUtc);

        var message = CatalogListingAccessGrantEventFactory.Create(
            grant,
            EventId,
            revokedAtUtc,
            CatalogEventContext.Create("catalog-access-revoke"));
        var integrationEvent = Deserialize(message.Payload);

        Assert.Equal(CatalogListingAccessGrantStateContract.Revoked, integrationEvent.State);
        Assert.Equal(2, integrationEvent.AggregateRevision);
        Assert.Equal(revokedAtUtc, integrationEvent.OccurredAtUtc);
        Assert.Equal(GrantedAtUtc, integrationEvent.GrantedAtUtc);
    }

    [Fact]
    public void EventTimestampMustMatchTheExactGrantTransition()
    {
        var grant = CreateGrant();

        var exception = Assert.Throws<CatalogContractException>(() =>
            CatalogListingAccessGrantEventFactory.Create(
                grant,
                EventId,
                GrantedAtUtc.AddSeconds(1),
                CatalogEventContext.Create("catalog-access-time")));

        Assert.Equal("catalog.access_grant_event_time_mismatch", exception.Code);
    }

    private static ListingAccessGrant CreateGrant()
    {
        var claim = ListingClaim.Submit(
            ClaimId,
            ListingId,
            OwnerActorId,
            "private://claim/evidence",
            new string('a', 64),
            GrantedAtUtc.AddMinutes(-1));
        return claim.Verify(
            GrantId,
            ReviewerActorId,
            Enum.GetValues<ListingAccessScope>(),
            GrantedAtUtc,
            GrantedAtUtc.AddDays(30));
    }

    private static CatalogListingAccessGrantChanged Deserialize(string payload)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return JsonSerializer.Deserialize<CatalogListingAccessGrantChanged>(payload, options)
            ?? throw new InvalidOperationException("Catalog access grant event payload was empty.");
    }
}
