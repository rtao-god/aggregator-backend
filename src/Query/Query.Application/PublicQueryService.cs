using System.Globalization;
using Aggregator.Query.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public sealed class PublicQueryService
{
    private readonly IPublicQueryStore _store;
    private readonly IQueryClock _clock;

    public PublicQueryService(IPublicQueryStore store, IQueryClock clock)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<PublicListingSearchResponse> SearchAsync(
        string catalogKey,
        PublicListingSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedCatalogKey = RequireKey(catalogKey, nameof(catalogKey));
        var criteria = CreateSearchCriteria(request);
        if (request.PageSize is < 1 or > 100)
        {
            throw new QueryReadException(
                "Query.PublicApi",
                "QUERY_PAGE_SIZE_INVALID",
                400,
                "Page size must be between 1 and 100.",
                "Submit a page size within the supported range.");
        }

        var queryDigest = QueryCursorCodec.ComputeQueryDigest(normalizedCatalogKey, criteria);
        QueryCursor? decodedCursor = null;
        if (request.Cursor is not null)
        {
            decodedCursor = QueryCursorCodec.Decode(request.Cursor);
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

        var readAtUtc = RequireUtc(_clock.GetUtcNow(), "Query public read clock");
        var snapshot = await _store.ReadPageAsync(
            normalizedCatalogKey,
            decodedCursor?.LastListingId,
            checked(request.PageSize + 1),
            criteria,
            readAtUtc,
            cancellationToken);
        if (snapshot is null)
        {
            throw ProjectionUnavailable(normalizedCatalogKey);
        }

        EnsureLocaleSupported(snapshot.LocalePolicy, criteria.RequestedLocale);
        EnsurePageContract(
            snapshot,
            normalizedCatalogKey,
            decodedCursor?.LastListingId,
            request.PageSize + 1,
            criteria,
            readAtUtc);
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

        var hasMore = snapshot.Documents.Count > request.PageSize;
        var page = snapshot.Documents.Take(request.PageSize).ToArray();
        var nextCursor = hasMore && page.Length > 0
            ? QueryCursorCodec.Encode(snapshot.Revision.Id, page[^1].ListingId, queryDigest)
            : null;
        var organic = page
            .Select(document => ToSummary(
                document,
                criteria.RequestedLocale,
                snapshot.LocalePolicy))
            .ToArray();
        var sponsored = snapshot.SponsoredDocuments
            .Select(item => ToSponsoredSummary(
                item,
                criteria.RequestedLocale,
                snapshot.LocalePolicy))
            .ToArray();
        return new PublicListingSearchResponse(
            ToMetadata(snapshot.Revision),
            new PublicListingSearchQuerySummary(
                criteria.RequestedLocale,
                criteria.CategoryKey,
                criteria.DistrictKey,
                criteria.ListingKind is null
                    ? null
                    : MapListingKind(criteria.ListingKind.Value),
                criteria.ContactKind is null
                    ? null
                    : MapContactKindContract(criteria.ContactKind.Value),
                criteria.MarketZone is null
                    ? null
                    : MapMarketZoneContract(criteria.MarketZone.Value)),
            sponsored,
            organic,
            snapshot.CategoryFacetCounts
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PublicFacetValue(item.Key, item.Value))
                .ToArray(),
            snapshot.DistrictFacetCounts
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => new PublicFacetValue(item.Key, item.Value))
                .ToArray(),
            snapshot.ListingKindFacetCounts
                .OrderBy(item => item.Key)
                .Select(item => new PublicListingKindFacetValue(
                    MapListingKind(item.Key),
                    item.Value))
                .ToArray(),
            snapshot.ContactKindFacetCounts
                .OrderBy(item => item.Key)
                .Select(item => new PublicContactKindFacetValue(
                    MapContactKindContract(item.Key),
                    item.Value))
                .ToArray(),
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
                .Select(item => new PublicContactValue(
                    item.ContactId,
                    MapContactKind(item.Kind),
                    item.Target,
                    item.Label))
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
        int maximumDocuments,
        PublicListingSearchCriteria criteria,
        DateTimeOffset readAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.LocalePolicy);
        ArgumentNullException.ThrowIfNull(snapshot.Documents);
        ArgumentNullException.ThrowIfNull(snapshot.SponsoredDocuments);
        ArgumentNullException.ThrowIfNull(snapshot.CategoryFacetCounts);
        ArgumentNullException.ThrowIfNull(snapshot.DistrictFacetCounts);
        ArgumentNullException.ThrowIfNull(snapshot.ListingKindFacetCounts);
        ArgumentNullException.ThrowIfNull(snapshot.ContactKindFacetCounts);
        ArgumentNullException.ThrowIfNull(criteria);
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

            EnsureDocumentMatchesCriteria(document, criteria);
            previous = document.ListingId;
        }

        EnsureStringFacets(snapshot.CategoryFacetCounts, "category");
        EnsureStringFacets(snapshot.DistrictFacetCounts, "district");
        EnsureEnumFacets(snapshot.ListingKindFacetCounts, "listing-kind");
        EnsureEnumFacets(snapshot.ContactKindFacetCounts, "contact-kind");

        var placementIds = new HashSet<Guid>();
        foreach (var sponsored in snapshot.SponsoredDocuments)
        {
            ArgumentNullException.ThrowIfNull(sponsored);
            ArgumentNullException.ThrowIfNull(sponsored.Placement);
            ArgumentNullException.ThrowIfNull(sponsored.Document);
            var placement = sponsored.Placement;
            if (!placementIds.Add(placement.PlacementId))
            {
                throw StoreContractFailure(
                    $"Query store returned sponsored placement '{placement.PlacementId}' more than once.");
            }

            if (!string.Equals(
                    placement.CatalogKey,
                    expectedCatalogKey,
                    StringComparison.Ordinal))
            {
                throw StoreContractFailure(
                    $"Sponsored placement '{placement.PlacementId}' belongs to another catalog.");
            }

            if (placement.ListingId != sponsored.Document.ListingId)
            {
                throw StoreContractFailure(
                    $"Sponsored placement '{placement.PlacementId}' is paired with another listing.");
            }

            EnsureDocumentMatchesCriteria(sponsored.Document, criteria);
            if (!placement.IsVisibleAt(readAtUtc))
            {
                throw StoreContractFailure(
                    $"Query store returned inactive or expired sponsored placement '{placement.PlacementId}'.");
            }

            if (!placement.LocaleScope.Contains(
                    criteria.RequestedLocale,
                    StringComparer.OrdinalIgnoreCase))
            {
                throw StoreContractFailure(
                    $"Sponsored placement '{placement.PlacementId}' does not target locale '{criteria.RequestedLocale}'.");
            }

            var scopeMatches = placement.Scope switch
            {
                QueryPromotionPlacementScope.Catalog =>
                    string.Equals(
                        placement.ScopeKey,
                        expectedCatalogKey,
                        StringComparison.Ordinal),
                QueryPromotionPlacementScope.Category =>
                    criteria.CategoryKey is not null &&
                    string.Equals(
                        placement.ScopeKey,
                        criteria.CategoryKey,
                        StringComparison.Ordinal),
                QueryPromotionPlacementScope.District =>
                    criteria.DistrictKey is not null &&
                    string.Equals(
                        placement.ScopeKey,
                        criteria.DistrictKey,
                        StringComparison.Ordinal),
                QueryPromotionPlacementScope.EditorialLanding => false,
                _ => false,
            };
            if (!scopeMatches)
            {
                throw StoreContractFailure(
                    $"Sponsored placement '{placement.PlacementId}' is outside the requested search scope.");
            }
        }
    }

    private static void EnsureDocumentMatchesCriteria(
        QueryListingDocument document,
        PublicListingSearchCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (criteria.CategoryKey is not null &&
            !document.CategoryKeys.Contains(criteria.CategoryKey, StringComparer.Ordinal))
        {
            throw StoreContractFailure(
                $"Listing '{document.ListingId}' does not match requested category '{criteria.CategoryKey}'.");
        }

        if (criteria.DistrictKey is not null &&
            !string.Equals(
                document.Geography.DistrictKey,
                criteria.DistrictKey,
                StringComparison.Ordinal))
        {
            throw StoreContractFailure(
                $"Listing '{document.ListingId}' does not match requested district '{criteria.DistrictKey}'.");
        }

        if (criteria.ListingKind is not null &&
            document.ListingKind != criteria.ListingKind.Value)
        {
            throw StoreContractFailure(
                $"Listing '{document.ListingId}' does not match requested listing kind '{criteria.ListingKind}'.");
        }

        if (criteria.ContactKind is not null &&
            !document.Contacts.Any(item => item.Kind == criteria.ContactKind.Value))
        {
            throw StoreContractFailure(
                $"Listing '{document.ListingId}' does not match requested contact kind '{criteria.ContactKind}'.");
        }

        if (criteria.MarketZone is not null &&
            document.Geography.State != criteria.MarketZone.Value)
        {
            throw StoreContractFailure(
                $"Listing '{document.ListingId}' does not match requested market zone '{criteria.MarketZone}'.");
        }
    }

    private static void EnsureStringFacets(
        IReadOnlyDictionary<string, int> facets,
        string facetKind)
    {
        if (facets.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) ||
                !string.Equals(item.Key, item.Key.Trim(), StringComparison.Ordinal) ||
                item.Value <= 0))
        {
            throw StoreContractFailure(
                $"Query store returned an invalid {facetKind} facet count.");
        }
    }

    private static void EnsureEnumFacets<TEnum>(
        IReadOnlyDictionary<TEnum, int> facets,
        string facetKind)
        where TEnum : struct, Enum
    {
        if (facets.Any(item => !Enum.IsDefined(item.Key) || item.Value <= 0))
        {
            throw StoreContractFailure(
                $"Query store returned an invalid {facetKind} facet count.");
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
            MapListingKind(document.ListingKind),
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

    private static PublicSponsoredListingSummary ToSponsoredSummary(
        PublicSponsoredListingSnapshot item,
        string requestedLocale,
        QueryLocalePolicy localePolicy)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Placement);
        ArgumentNullException.ThrowIfNull(item.Document);
        return new PublicSponsoredListingSummary(
            item.Placement.PlacementId,
            item.Placement.EntitlementId,
            item.Placement.ProductKey,
            item.Placement.Scope switch
            {
                QueryPromotionPlacementScope.Catalog => "catalog",
                QueryPromotionPlacementScope.Category => "category",
                QueryPromotionPlacementScope.District => "district",
                QueryPromotionPlacementScope.EditorialLanding => "editorial_landing",
                _ => throw StoreContractFailure(
                    $"Unsupported sponsored placement scope '{item.Placement.Scope}'."),
            },
            item.Placement.ScopeKey,
            item.Placement.PriorityBand,
            item.Placement.CapacitySlot,
            item.Placement.PresentationLabelKey,
            item.Placement.StartsAtUtc,
            item.Placement.HardExpiryAtUtc,
            ToSummary(item.Document, requestedLocale, localePolicy));
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

    private static PublicListingSearchCriteria CreateSearchCriteria(
        PublicListingSearchRequest request)
    {
        var listingKind = request.ListingKind switch
        {
            null => null,
            PublicListingKindContract.Place => QueryListingKind.Place,
            PublicListingKindContract.Provider => QueryListingKind.Provider,
            _ => throw InvalidFilter(
                nameof(request.ListingKind),
                request.ListingKind,
                "Use one of the declared public listing-kind values."),
        };
        var contactKind = request.ContactKind switch
        {
            null => null,
            PublicContactKindContract.Website => QueryContactKind.Website,
            PublicContactKindContract.Email => QueryContactKind.Email,
            PublicContactKindContract.Phone => QueryContactKind.Phone,
            PublicContactKindContract.WhatsApp => QueryContactKind.WhatsApp,
            PublicContactKindContract.BookingReference => QueryContactKind.BookingReference,
            PublicContactKindContract.MapReference => QueryContactKind.MapReference,
            _ => throw InvalidFilter(
                nameof(request.ContactKind),
                request.ContactKind,
                "Use one of the declared public contact-kind values."),
        };
        var marketZone = request.MarketZone switch
        {
            null => null,
            PublicMarketZoneContract.PrimaryMarket => QueryGeographyState.PrimaryMarket,
            PublicMarketZoneContract.NearbyMarket => QueryGeographyState.NearbyMarket,
            PublicMarketZoneContract.RemoteOnly => QueryGeographyState.RemoteOnly,
            PublicMarketZoneContract.OutsideMarket => QueryGeographyState.OutsideMarket,
            _ => throw InvalidFilter(
                nameof(request.MarketZone),
                request.MarketZone,
                "Use one of the declared public market-zone values."),
        };
        return new PublicListingSearchCriteria(
            RequireLocale(request.Locale, nameof(request.Locale)),
            NormalizeOptionalKey(request.CategoryKey, nameof(request.CategoryKey)),
            NormalizeOptionalKey(request.DistrictKey, nameof(request.DistrictKey)),
            listingKind,
            contactKind,
            marketZone);
    }

    private static string? NormalizeOptionalKey(string? value, string parameterName) =>
        value is null ? null : RequireKey(value, parameterName);

    private static QueryReadException InvalidFilter(
        string parameterName,
        object? value,
        string requiredAction) =>
        new(
            "Query.Search",
            "QUERY_FILTER_INVALID",
            400,
            $"Filter '{parameterName}' has unsupported value '{value}'.",
            requiredAction);

    private static PublicListingKindContract MapListingKind(QueryListingKind value) => value switch
    {
        QueryListingKind.Place => PublicListingKindContract.Place,
        QueryListingKind.Provider => PublicListingKindContract.Provider,
        _ => throw StoreContractFailure($"Unsupported listing kind '{value}'."),
    };

    private static PublicContactKindContract MapContactKindContract(
        QueryContactKind value) => value switch
    {
        QueryContactKind.Website => PublicContactKindContract.Website,
        QueryContactKind.Email => PublicContactKindContract.Email,
        QueryContactKind.Phone => PublicContactKindContract.Phone,
        QueryContactKind.WhatsApp => PublicContactKindContract.WhatsApp,
        QueryContactKind.BookingReference => PublicContactKindContract.BookingReference,
        QueryContactKind.MapReference => PublicContactKindContract.MapReference,
        _ => throw StoreContractFailure($"Unsupported contact kind '{value}'."),
    };

    private static PublicMarketZoneContract MapMarketZoneContract(
        QueryGeographyState value) => value switch
    {
        QueryGeographyState.PrimaryMarket => PublicMarketZoneContract.PrimaryMarket,
        QueryGeographyState.NearbyMarket => PublicMarketZoneContract.NearbyMarket,
        QueryGeographyState.RemoteOnly => PublicMarketZoneContract.RemoteOnly,
        QueryGeographyState.OutsideMarket => PublicMarketZoneContract.OutsideMarket,
        _ => throw StoreContractFailure($"Unsupported market zone '{value}'."),
    };

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

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string owner)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw StoreContractFailure($"{owner} returned a non-UTC timestamp.");
        }

        return value;
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
