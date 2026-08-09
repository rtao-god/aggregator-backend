using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>
/// Creates one producer-owned listing eligibility event after Catalog persistence allocates its exact revision.
/// </summary>
public delegate CatalogOutboxMessage CatalogListingPromotionEligibilityOutboxFactory(
    long eligibilityRevision);

/// <summary>
/// Prepared Catalog effect containing one exact listing state and the matching producer event factory.
/// </summary>
public sealed record CatalogListingPromotionEligibilityOutboxRequest(
    Listing Listing,
    CatalogListingPromotionEligibilityOutboxFactory OutboxFactory)
{
    public CatalogKey CatalogKey => Listing.CatalogKey;

    public Guid ListingId => Listing.Id;
}

/// <summary>
/// Creates one Catalog-owned eligibility event request whose revision is allocated in the business transaction.
/// </summary>
public static class CatalogListingPromotionEligibilityEventFactory
{
    public static CatalogListingPromotionEligibilityOutboxRequest CreatePublished(
        Listing listing,
        ListingRevision publishedRevision,
        bool hasBlockingDispute,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CatalogEventContext eventContext)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(publishedRevision);
        ArgumentNullException.ThrowIfNull(eventContext);
        RequireEventIdentity(eventId, occurredAtUtc);
        if (listing.State == ListingLifecycleState.Archived)
        {
            throw new CatalogConflictException(
                $"Archived listing '{listing.Id}' cannot become Promotion-eligible.");
        }

        if (publishedRevision.ListingId != listing.Id)
        {
            throw new CatalogConflictException(
                $"Listing revision '{publishedRevision.Id}' does not belong to listing '{listing.Id}'.");
        }

        var verifiedCapabilities = publishedRevision.Content.Contacts
            .Where(contact => IsOwnerVerified(publishedRevision.Content, contact))
            .Select(contact => ToCapability(contact.Kind))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var categoryKeys = publishedRevision.Content.Categories
            .Select(category => category.CategoryKey.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return CreateRequest(
            listing,
            eventId,
            publishedRevision.Id,
            isPublished: true,
            isArchived: false,
            hasBlockingDispute,
            verifiedCapabilities,
            categoryKeys,
            publishedRevision.Content.Geography.DistrictKey,
            occurredAtUtc,
            eventContext);
    }

    public static CatalogListingPromotionEligibilityOutboxRequest CreateUnavailable(
        Listing listing,
        bool hasBlockingDispute,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        CatalogEventContext eventContext)
    {
        ArgumentNullException.ThrowIfNull(listing);
        ArgumentNullException.ThrowIfNull(eventContext);
        RequireEventIdentity(eventId, occurredAtUtc);
        return CreateRequest(
            listing,
            eventId,
            publishedListingRevisionId: null,
            isPublished: false,
            isArchived: listing.State == ListingLifecycleState.Archived,
            hasBlockingDispute,
            verifiedContactCapabilities: Array.Empty<string>(),
            categoryKeys: Array.Empty<string>(),
            districtKey: null,
            occurredAtUtc,
            eventContext);
    }

    private static CatalogListingPromotionEligibilityOutboxRequest CreateRequest(
        Listing listing,
        Guid eventId,
        Guid? publishedListingRevisionId,
        bool isPublished,
        bool isArchived,
        bool hasBlockingDispute,
        IReadOnlyList<string> verifiedContactCapabilities,
        IReadOnlyList<string> categoryKeys,
        string? districtKey,
        DateTimeOffset occurredAtUtc,
        CatalogEventContext eventContext)
    {
        CatalogOutboxMessage CreateOutbox(long eligibilityRevision)
        {
            var integrationEvent = new CatalogListingPromotionEligibilityChanged(
                eventId,
                listing.CatalogKey.Value,
                listing.Id,
                publishedListingRevisionId,
                isPublished,
                isArchived,
                hasBlockingDispute,
                verifiedContactCapabilities.Count > 0,
                verifiedContactCapabilities,
                categoryKeys,
                districtKey,
                eligibilityRevision,
                occurredAtUtc);
            return CatalogOutboxMessageFactory.Create(
                integrationEvent.EventId,
                CatalogIntegrationEventTypes.ListingPromotionEligibilityChanged,
                CatalogIntegrationEventContracts.ListingPromotionEligibilityChanged,
                integrationEvent,
                occurredAtUtc,
                eventContext);
        }

        return new CatalogListingPromotionEligibilityOutboxRequest(
            listing,
            CreateOutbox);
    }

    private static bool IsOwnerVerified(ListingRevisionContent content, ContactValue contact)
    {
        if (!content.Assertions.TryGetValue(contact.AssertionId, out var assertion))
        {
            throw new CatalogContractException(
                "catalog.promotion_contact_assertion_missing",
                $"Contact '{contact.Id}' references missing assertion '{contact.AssertionId}'.");
        }

        return assertion.SourceKind == SourceKind.OwnerVerification;
    }

    private static string ToCapability(ContactKind kind) =>
        kind switch
        {
            ContactKind.Website => CatalogPromotionContactCapabilities.Website,
            ContactKind.Email => CatalogPromotionContactCapabilities.Email,
            ContactKind.Phone => CatalogPromotionContactCapabilities.Phone,
            ContactKind.WhatsApp => CatalogPromotionContactCapabilities.WhatsApp,
            ContactKind.BookingReference => CatalogPromotionContactCapabilities.BookingReference,
            ContactKind.MapReference => CatalogPromotionContactCapabilities.MapReference,
            _ => throw new CatalogContractException(
                "catalog.promotion_contact_kind_unknown",
                $"Contact kind '{kind}' has no Promotion capability contract."),
        };

    private static void RequireEventIdentity(Guid eventId, DateTimeOffset occurredAtUtc)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("Eligibility event ID is required.", nameof(eventId));
        }

        if (occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new CatalogContractException(
                "catalog.promotion_eligibility_timestamp_not_utc",
                "Catalog Promotion eligibility event timestamp must be normalized to UTC.");
        }
    }
}
