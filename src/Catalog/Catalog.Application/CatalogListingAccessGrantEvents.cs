using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Creates the producer-owned Catalog access-grant event from one exact grant revision.</summary>
public static class CatalogListingAccessGrantEventFactory
{
    public static CatalogOutboxMessage Create(
        ListingAccessGrant grant,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CatalogEventContext eventContext)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(eventContext);
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Listing access grant event ID is required.", nameof(eventId));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new CatalogContractException(
                "catalog.access_grant_event_time_not_utc",
                "Listing access grant event timestamp must be normalized to UTC.");
        }

        var state = grant.RevokedAtUtc is null
            ? CatalogListingAccessGrantStateContract.Active
            : CatalogListingAccessGrantStateContract.Revoked;
        var expectedOccurredAtUtc = grant.RevokedAtUtc ?? grant.GrantedAtUtc;
        if (occurredAtUtc != expectedOccurredAtUtc)
        {
            throw new CatalogContractException(
                "catalog.access_grant_event_time_mismatch",
                "Listing access grant event timestamp must match the exact grant revision transition.");
        }

        var integrationEvent = new CatalogListingAccessGrantChanged(
            eventId,
            grant.Id,
            grant.ListingId,
            grant.ActorId,
            CatalogListingAccessGrantContractMapper.ToContracts(grant.Scopes),
            state,
            grant.GrantedAtUtc,
            grant.ExpiresAtUtc,
            grant.AggregateRevision,
            occurredAtUtc);
        return CatalogOutboxMessageFactory.Create(
            integrationEvent.EventId,
            CatalogIntegrationEventTypes.ListingAccessGrantChanged,
            CatalogIntegrationEventContracts.ListingAccessGrantChanged,
            integrationEvent,
            occurredAtUtc,
            eventContext);
    }
}
