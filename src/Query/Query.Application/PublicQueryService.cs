using System.Globalization;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed class PublicQueryService
{
    private readonly IPublicQueryStore _store;

    public PublicQueryService(IPublicQueryStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<PublicListingSearchResponse> SearchAsync(
        string catalogKey,
        string locale,
        string? categoryKey,
        int pageSize,
        string? cursor,
        CancellationToken cancellationToken)
    {
        var normalizedCatalogKey = RequireKey(catalogKey, nameof(catalogKey));
        var normalizedLocale = RequireLocale(locale, nameof(locale));
        var normalizedCategoryKey = categoryKey is null
            ? null
            : RequireKey(categoryKey, nameof(categoryKey));
        if (pageSize is < 1 or > 100)
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_PAGE_SIZE_INVALID",
                400,
                "Page size must be between 1 and 100.",
                "Submit a page size within the supported range.");
        }

        var queryDigest = QueryCursorCodec.ComputeQueryDigest(
            normalizedCatalogKey,
            normalizedLocale,
            normalizedCategoryKey);
        QueryCursor? decodedCursor = null;
        if (cursor is not null)
        {
            decodedCursor = QueryCursorCodec.Decode(cursor);
            if (!string.Equals(decodedCursor.Value.QueryDigest, queryDigest, StringComparison.Ordinal))
            {
                throw new QueryReadException(
                    "Query.Cursor",
                    "QUERY_CURSOR_SCOPE_MISMATCH",
                    400,
                    "Cursor belongs to a different normalized query.",
                    "Restart the search without the cursor.");
            }
        }

        var snapshot = await _store.ReadPageAsync(
            normalizedCatalogKey,
            decodedCursor?.LastListingId,
            checked(pageSize + 1),
            normalizedCategoryKey,
            cancellationToken);
        if (snapshot is null)
        {
            throw ProjectionUnavailable(normalizedCatalogKey);
        }

        EnsureLocaleSupported(snapshot.LocalePolicy, normalizedLocale);
        EnsurePageContract(snapshot, normalizedCatalogKey, decodedCursor?.LastListingId, pageSize + 1);
        if (decodedCursor is { } expected && expected.PublicReadRevisionId != snapshot.Revision.Id)
        {
            throw new QueryReadException(
                "Query.Cursor",
                "QUERY_CURSOR_REVISION_MISMATCH",
                409,
                "Cursor belongs to a public read revision that is no longer current.",
                "Restart the search without the cursor.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["cursorRevisionId"] = expected.PublicReadRevisionId,
                    ["currentRevisionId"] = snapshot.Revision.Id,
                });
        }

        var hasMore = snapshot.Documents.Count > pageSize;
        var page = snapshot.Documents.Take(pageSize).ToArray();
        var nextCursor = hasMore && page.Length > 0
            ? QueryCursorCodec.Encode(snapshot.Revision.Id, page[^1].ListingId, queryDigest)
            : null;
        var organic = page
            .Select(document => ToSummary(document, normalizedLocale, snapshot.LocalePolicy))
            .ToArray();
        var facets = snapshot.CategoryFacetCounts
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new PublicFacetValue(item.Key, item.Value))
            .ToArray();
        return new PublicListingSearchResponse(
            ToMetadata(snapshot.Revision),
            Array.Empty<PublicListingSummary>(),
            organic,
            facets,
            nextCursor);
    }

    public async Task<PublicListingCardResponse> GetByRouteAsync(
        string catalogKey,
        string routePath,
        string requestedLocale,
        CancellationToken cancellationToken)
    {
        var normalizedCatalogKey = RequireKey(catalogKey, nameof(catalogKey));
        var normalizedRoutePath = RequireText(routePath, nameof(routePath), 500);
        var normalizedLocale = RequireLocale(requestedLocale, nameof(requestedLocale));
        if (!normalizedRoutePath.StartsWith('/'))
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_ROUTE_PATH_INVALID",
                400,
                "Route path must be absolute within the site.",
                "Submit a route path beginning with '/'.");
        }

        var snapshot = await _store.ReadByRouteAsync(
            normalizedCatalogKey,
            normalizedRoutePath,
            cancellationToken);
        if (snapshot is null)
        {
            throw ProjectionUnavailable(normalizedCatalogKey);
        }

        EnsureRevisionCatalog(snapshot.Revision, normalizedCatalogKey);
        EnsureLocaleSupported(snapshot.LocalePolicy, normalizedLocale);
        if (snapshot.Document is null)
        {
            throw new QueryReadException(
                "Query.Routes",
                "QUERY_ROUTE_NOT_FOUND",
                404,
                $"Public route '{normalizedRoutePath}' does not exist in the active revision.",
                "Use a route emitted by the active Query projection.");
        }

        var document = snapshot.Document;
        var summary = ToSummary(document, normalizedLocale, snapshot.LocalePolicy);
        return new PublicListingCardResponse(
            ToMetadata(snapshot.Revision),
            summary,
            document.Attributes.Select(ToContract).ToArray(),
            new PublicGeographyValue(
                MapGeographyState(document.Geography.State),
                document.Geography.Latitude,
                document.Geography.Longitude,
                document.Geography.DistrictKey),
            document.Contacts
                .Select(item => new PublicContactValue(MapContactKind(item.Kind), item.Target, item.Label))
                .ToArray(),
            document.Media
                .Select(item => new PublicMediaValue(
                    item.MediaId,
                    item.ObjectUri,
                    item.ContentType,
                    item.ContentDigest,
                    MapRightsBasis(item.RightsBasis)))
                .ToArray());
    }

    private static void EnsurePageContract(
        PublicReadPageSnapshot snapshot,
        string expectedCatalogKey,
        Guid? afterListingId,
        int maximumDocuments)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.LocalePolicy);
        ArgumentNullException.ThrowIfNull(snapshot.Documents);
        ArgumentNullException.ThrowIfNull(snapshot.CategoryFacetCounts);
        EnsureRevisionCatalog(snapshot.Revision, expectedCatalogKey);
        if (snapshot.Documents.Count > maximumDocuments)
        {
            throw StoreContractFailure("Query store returned more documents than requested.");
        }

        Guid? previous = afterListingId;
        foreach (var document in snapshot.Documents)
        {
            if (previous is not null && document.ListingId.CompareTo(previous.Value) <= 0)
            {
                throw StoreContractFailure("Query store returned an unordered or duplicate listing page.");
            }

            previous = document.ListingId;
        }

        if (snapshot.CategoryFacetCounts.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value < 0))
        {
            throw StoreContractFailure("Query store returned an invalid category facet count.");
        }
    }

    private static void EnsureRevisionCatalog(PublicReadRevision revision, string expectedCatalogKey)
    {
        ArgumentNullException.ThrowIfNull(revision);
        if (!string.Equals(revision.CatalogKey, expectedCatalogKey, StringComparison.Ordinal))
        {
            throw StoreContractFailure("Query store returned a revision owned by another catalog.");
        }
    }

    private static void EnsureLocaleSupported(QueryLocalePolicy localePolicy, string requestedLocale)
    {
        ArgumentNullException.ThrowIfNull(localePolicy);
        if (!localePolicy.Supports(requestedLocale))
        {
            throw new QueryReadException(
                "Query.Localization",
                "QUERY_LOCALE_UNSUPPORTED",
                400,
                $"Locale '{requestedLocale}' is not supported by the active public read revision.",
                "Submit one of the locales declared by the active Query projection.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["requestedLocale"] = requestedLocale,
                    ["supportedLocales"] = localePolicy.SupportedLocales,
                });
        }
    }

    private static PublicListingSummary ToSummary(
        QueryListingDocument document,
        string requestedLocale,
        QueryLocalePolicy localePolicy)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(localePolicy);
        var localization = ResolveLocalization(document, requestedLocale, localePolicy);
        return new PublicListingSummary(
            document.ListingId,
            document.ListingRevisionId,
            document.ListingKind switch
            {
                QueryListingKind.Place => PublicListingKindContract.Place,
                QueryListingKind.Provider => PublicListingKindContract.Provider,
                _ => throw StoreContractFailure($"Unsupported listing kind '{document.ListingKind}'."),
            },
            requestedLocale,
            localization.Value.Locale,
            localization.Exact ? "exact" : "fallback",
            localization.Value.RoutePath,
            localization.Value.Title,
            MapFieldState(localization.Value.DescriptionState),
            localization.Value.Description,
            document.CategoryKeys,
            document.Geography.DistrictKey);
    }

    private static ResolvedLocalization ResolveLocalization(
        QueryListingDocument document,
        string requestedLocale,
        QueryLocalePolicy localePolicy)
    {
        var exact = document.Localizations.FirstOrDefault(item =>
            string.Equals(item.Locale, requestedLocale, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return new ResolvedLocalization(exact, true);
        }

        var fallback = document.Localizations.FirstOrDefault(item =>
            string.Equals(item.Locale, localePolicy.DefaultLocale, StringComparison.OrdinalIgnoreCase));
        if (fallback is null)
        {
            throw StoreContractFailure(
                $"Listing '{document.ListingId}' lacks the active default locale '{localePolicy.DefaultLocale}'.");
        }

        return new ResolvedLocalization(fallback, false);
    }

    private static PublicAttributeValue ToContract(QueryAttributeDocument attribute) =>
        new(
            attribute.AttributeKey,
            MapFieldState(attribute.State),
            attribute.ValueKind is null ? null : MapValueKind(attribute.ValueKind.Value),
            attribute.BooleanValue,
            attribute.DecimalValue,
            attribute.TextValue,
            attribute.TextCollectionValue);

    private static PublicReadMetadata ToMetadata(PublicReadRevision revision) =>
        new(
            revision.Id,
            revision.BaseProjectionId,
            revision.PromotionOverlayId,
            revision.SafetyOverlayId,
            revision.SourcePublicationId,
            revision.CreatedAtUtc);

    private static PublicFieldStateContract MapFieldState(QueryFieldState state) => state switch
    {
        QueryFieldState.Observed => PublicFieldStateContract.Observed,
        QueryFieldState.Missing => PublicFieldStateContract.Missing,
        QueryFieldState.NotApplicable => PublicFieldStateContract.NotApplicable,
        QueryFieldState.Withheld => PublicFieldStateContract.Withheld,
        _ => throw StoreContractFailure($"Unsupported field state '{state}'."),
    };

    private static string MapValueKind(QueryValueKind value) => value switch
    {
        QueryValueKind.BooleanValue => "boolean",
        QueryValueKind.DecimalNumber => "decimal",
        QueryValueKind.TextValue => "text",
        QueryValueKind.TextCollection => "text_collection",
        QueryValueKind.DurationMinutes => "duration_minutes",
        _ => throw StoreContractFailure($"Unsupported value kind '{value}'."),
    };

    private static string MapContactKind(QueryContactKind value) => value switch
    {
        QueryContactKind.Website => "website",
        QueryContactKind.Email => "email",
        QueryContactKind.Phone => "phone",
        QueryContactKind.WhatsApp => "whatsapp",
        QueryContactKind.BookingReference => "booking_reference",
        QueryContactKind.MapReference => "map_reference",
        _ => throw StoreContractFailure($"Unsupported contact kind '{value}'."),
    };

    private static string MapRightsBasis(QueryMediaRightsBasis value) => value switch
    {
        QueryMediaRightsBasis.OwnerProvided => "owner_provided",
        QueryMediaRightsBasis.ExplicitLicense => "explicit_license",
        QueryMediaRightsBasis.OriginalEditorialWork => "original_editorial_work",
        QueryMediaRightsBasis.PublicDomain => "public_domain",
        _ => throw StoreContractFailure($"Unsupported media rights basis '{value}'."),
    };

    private static string MapGeographyState(QueryGeographyState value) => value switch
    {
        QueryGeographyState.PrimaryMarket => "primary_market",
        QueryGeographyState.NearbyMarket => "nearby_market",
        QueryGeographyState.RemoteOnly => "remote_only",
        QueryGeographyState.OutsideMarket => "outside_market",
        _ => throw StoreContractFailure($"Unsupported geography state '{value}'."),
    };

    private static string RequireKey(string value, string parameterName) =>
        RequireText(value, parameterName, 200);

    private static string RequireLocale(string value, string parameterName)
    {
        var normalized = RequireText(value, parameterName, 35);
        try
        {
            return CultureInfo.GetCultureInfo(normalized).Name;
        }
        catch (CultureNotFoundException exception)
        {
            throw new QueryReadException(
                "Query.Localization",
                "QUERY_LOCALE_INVALID",
                400,
                $"Locale '{normalized}' is not recognized.",
                "Submit a valid locale identifier.",
                innerException: exception);
        }
    }

    private static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_PARAMETER_REQUIRED",
                400,
                $"Parameter '{parameterName}' is required.",
                "Submit a non-empty parameter value.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_PARAMETER_TOO_LONG",
                400,
                $"Parameter '{parameterName}' exceeds {maximumLength} characters.",
                "Submit a shorter parameter value.");
        }

        return normalized;
    }

    private static QueryReadException ProjectionUnavailable(string catalogKey) =>
        new(
            "Query.PublicReadRevision",
            "QUERY_PROJECTION_UNAVAILABLE",
            503,
            $"Catalog '{catalogKey}' has no active public read revision.",
            "Activate a valid Catalog publication and complete Query projection build.");

    private static QueryReadException StoreContractFailure(string message) =>
        new(
            "Query.Persistence",
            "QUERY_STORE_CONTRACT_INVALID",
            500,
            message,
            "Inspect the Query projection store and active revision before serving public traffic.");

    private sealed record ResolvedLocalization(QueryLocalizedDocument Value, bool Exact);
}
