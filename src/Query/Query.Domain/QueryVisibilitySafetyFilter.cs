namespace Aggregator.Query.Domain;

/// <summary>
/// Applies one effective set of active Catalog suppressions to an immutable Query document without
/// adding or changing public content.
/// </summary>
public sealed class QueryVisibilitySafetyFilter
{
    private readonly IReadOnlyList<QueryVisibilitySuppression> _items;
    private readonly HashSet<Guid> _suppressedMediaIds;
    private readonly HashSet<Guid> _suppressedContactIds;

    private QueryVisibilitySafetyFilter(IReadOnlyList<QueryVisibilitySuppression> items)
    {
        _items = items;
        _suppressedMediaIds = items
            .Where(item => item.TargetKind == QueryVisibilitySuppressionTargetKind.Media)
            .Select(item => Guid.Parse(item.TargetKey))
            .ToHashSet();
        _suppressedContactIds = items
            .Where(item => item.TargetKind == QueryVisibilitySuppressionTargetKind.Contact)
            .Select(item => Guid.Parse(item.TargetKey))
            .ToHashSet();
    }

    public static QueryVisibilitySafetyFilter Create(
        IEnumerable<QueryVisibilitySuppression> suppressions)
    {
        ArgumentNullException.ThrowIfNull(suppressions);
        var items = suppressions
            .OrderBy(item => item.SuppressionId)
            .ToArray();
        if (items.Any(item => item.State != QueryVisibilitySuppressionState.Active))
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_EFFECTIVE_SET_INVALID",
                "A public-read safety filter can contain only active suppressions.");
        }

        if (items.Select(item => item.SuppressionId).Distinct().Count() != items.Length)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_SUPPRESSION_DUPLICATE",
                "A public-read safety filter cannot contain the same suppression more than once.");
        }

        var conflictingTarget = items
            .GroupBy(item => new { item.TargetKind, item.TargetKey })
            .FirstOrDefault(group => group
                .Select(item => item.ResponseMode)
                .Distinct()
                .Skip(1)
                .Any());
        if (conflictingTarget is not null)
        {
            throw new QueryDomainException(
                "QUERY_VISIBILITY_TARGET_RESPONSE_CONFLICT",
                $"Visibility target '{conflictingTarget.Key.TargetKind}:{conflictingTarget.Key.TargetKey}' has conflicting public response modes.");
        }

        return new QueryVisibilitySafetyFilter(Array.AsReadOnly(items));
    }

    public bool IsListingVisible(QueryListingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return FindListingSuppression(document.ListingId) is null &&
               !document.Localizations.Any(localization =>
                   FindRouteSuppression(localization.RoutePath) is not null);
    }

    public QueryVisibilitySuppression? FindListingSuppression(Guid listingId) =>
        _items.FirstOrDefault(item =>
            item.TargetKind == QueryVisibilitySuppressionTargetKind.Listing &&
            item.ListingId == listingId);

    public QueryVisibilitySuppression? FindRouteSuppression(string routePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routePath);
        return _items.FirstOrDefault(item =>
            item.TargetKind == QueryVisibilitySuppressionTargetKind.Route &&
            string.Equals(item.TargetKey, routePath, StringComparison.Ordinal));
    }

    public QueryListingDocument FilterChildren(QueryListingDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var filteredContacts = document.Contacts
            .Where(item => !_suppressedContactIds.Contains(item.ContactId))
            .ToArray();
        var filteredMedia = document.Media
            .Where(item => !_suppressedMediaIds.Contains(item.MediaId))
            .ToArray();
        if (filteredContacts.Length == document.Contacts.Count &&
            filteredMedia.Length == document.Media.Count)
        {
            return document;
        }

        return QueryListingDocument.Create(
            document.ListingId,
            document.ListingRevisionId,
            document.SubjectId,
            document.SubjectRevisionId,
            document.ListingKind,
            document.Localizations,
            document.CategoryKeys,
            document.Attributes,
            document.Geography,
            filteredContacts,
            filteredMedia,
            document.SourceContentDigest,
            document.PublishedAtUtc);
    }
}
