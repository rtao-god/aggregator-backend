using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Owns the complete Catalog access-grant contract mapping, including cross-context permissions.</summary>
public static class CatalogListingAccessGrantContractMapper
{
    public static ListingAccessScope ToDomain(ListingAccessScopeContract value) =>
        value switch
        {
            ListingAccessScopeContract.ReadDraft => ListingAccessScope.ReadDraft,
            ListingAccessScopeContract.ProposeRevision => ListingAccessScope.ProposeRevision,
            ListingAccessScopeContract.ManageContacts => ListingAccessScope.ManageContacts,
            ListingAccessScopeContract.ManageMedia => ListingAccessScope.ManageMedia,
            ListingAccessScopeContract.ViewAnalytics => ListingAccessScope.ViewAnalytics,
            ListingAccessScopeContract.ManagePromotion => ListingAccessScope.ManagePromotion,
            ListingAccessScopeContract.ManageMembers => ListingAccessScope.ManageMembers,
            _ => throw new CatalogContractException(
                "catalog.access_scope_unsupported",
                $"Listing access scope '{value}' is unsupported."),
        };

    public static ListingAccessScopeContract ToContract(ListingAccessScope value) =>
        value switch
        {
            ListingAccessScope.ReadDraft => ListingAccessScopeContract.ReadDraft,
            ListingAccessScope.ProposeRevision => ListingAccessScopeContract.ProposeRevision,
            ListingAccessScope.ManageContacts => ListingAccessScopeContract.ManageContacts,
            ListingAccessScope.ManageMedia => ListingAccessScopeContract.ManageMedia,
            ListingAccessScope.ViewAnalytics => ListingAccessScopeContract.ViewAnalytics,
            ListingAccessScope.ManagePromotion => ListingAccessScopeContract.ManagePromotion,
            ListingAccessScope.ManageMembers => ListingAccessScopeContract.ManageMembers,
            _ => throw new CatalogContractException(
                "catalog.access_scope_unsupported",
                $"Listing access scope '{value}' is unsupported."),
        };

    public static IReadOnlyList<ListingAccessScopeContract> ToContracts(
        IEnumerable<ListingAccessScope> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var result = scopes
            .Select(ToContract)
            .Distinct()
            .OrderBy(scope => (int)scope)
            .ToArray();
        if (result.Length == 0)
        {
            throw new CatalogContractException(
                "catalog.access_scope_required",
                "Listing access grant requires at least one permission.");
        }

        return result;
    }

    public static ListingAccessGrantResponse ToResponse(ListingAccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new ListingAccessGrantResponse(
            grant.Id,
            grant.ListingId,
            grant.ActorId,
            ToContracts(grant.Scopes),
            grant.GrantedAtUtc,
            grant.ExpiresAtUtc,
            grant.ClaimId,
            grant.RevokedAtUtc);
    }
}
