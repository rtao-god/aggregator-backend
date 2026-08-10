using Aggregator.Catalog.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public static class CatalogPublicationProjectionBuilder
{
    public const string BuilderIdentity = "aggregator-query-projection";

    public static QueryProjectionActivation Build(
        CatalogPublicationActivated activation,
        CatalogPublicationArtifact artifact,
        Guid baseProjectionId,
        Guid promotionOverlayId,
        Guid safetyOverlayId,
        Guid publicReadRevisionId,
        DateTimeOffset builtAtUtc)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(artifact);
        ValidateSourceIdentity(activation, artifact);

        var localePolicy = QueryLocalePolicy.Create(artifact.DefaultLocale, artifact.SupportedLocales);
        var listingRoutes = BuildListingRouteMap(artifact, localePolicy);
        var documents = artifact.Listings
            .OrderBy(item => item.ListingId)
            .Select(item => MapDocument(
                item,
                artifact.CreatedAtUtc,
                listingRoutes))
            .ToArray();
        var baseDigest = QueryCanonicalJson.ComputeDigest(new
        {
            artifact.PublicationId,
            artifact.CatalogKey,
            localePolicy.DefaultLocale,
            localePolicy.SupportedLocales,
            artifact.PublicationSequence,
            artifact.ConfigurationRevisionId,
            builder = BuilderIdentity,
            documents,
        });
        var baseProjection = QueryBaseProjection.Create(
            baseProjectionId,
            artifact.CatalogKey,
            localePolicy,
            artifact.PublicationId,
            activation.ArtifactDigest,
            artifact.PublicationSequence,
            BuilderIdentity,
            builtAtUtc,
            documents,
            baseDigest);
        var promotionDigest = QueryCanonicalJson.ComputeDigest(new
        {
            artifact.CatalogKey,
            kind = QueryOverlayKind.Promotion,
            sourceRevision = 0,
            items = Array.Empty<object>(),
        });
        var safetyDigest = QueryCanonicalJson.ComputeDigest(new
        {
            artifact.CatalogKey,
            kind = QueryOverlayKind.VisibilitySafety,
            sourceRevision = artifact.PublicationSequence,
            items = Array.Empty<object>(),
        });
        var promotionOverlay = QueryOverlayRevision.CreateEmpty(
            promotionOverlayId,
            artifact.CatalogKey,
            QueryOverlayKind.Promotion,
            0,
            builtAtUtc,
            promotionDigest);
        var safetyOverlay = QueryOverlayRevision.CreateEmpty(
            safetyOverlayId,
            artifact.CatalogKey,
            QueryOverlayKind.VisibilitySafety,
            artifact.PublicationSequence,
            builtAtUtc,
            safetyDigest);
        var seoDocuments = PublicSeoProjectionDocumentBuilder.Build(
            BuildSeoRouteSources(artifact, localePolicy));
        var seoProjection = PublicSitemapProjectionArtifactBuilder.Build(
            publicReadRevisionId,
            expectedCurrentPublicReadRevisionId: null,
            artifact.CatalogKey,
            seoDocuments.SitemapRecords,
            seoDocuments.Redirects,
            builtAtUtc);
        var publicReadDigest = QueryCanonicalJson.ComputeDigest(new
        {
            baseProjectionDigest = baseProjection.ContentDigest,
            promotionOverlayDigest = promotionOverlay.ContentDigest,
            safetyOverlayDigest = safetyOverlay.ContentDigest,
            seoProjectionDigest = seoProjection.ContentDigest,
            artifact.PublicationId,
        });
        var publicReadRevision = PublicReadRevision.Create(
            publicReadRevisionId,
            baseProjection,
            promotionOverlay,
            safetyOverlay,
            builtAtUtc,
            publicReadDigest);
        return new QueryProjectionActivation(
            baseProjection,
            promotionOverlay,
            safetyOverlay,
            publicReadRevision,
            seoProjection);
    }

    private static QueryListingDocument MapDocument(
        PublicListingDocument source,
        DateTimeOffset publishedAtUtc,
        IReadOnlyDictionary<ListingRouteIdentity, PublicRouteDocument> listingRoutes)
    {
        var listingKind = source.SubjectKind switch
        {
            SubjectKindContract.Place => QueryListingKind.Place,
            SubjectKindContract.Provider => QueryListingKind.Provider,
            _ => throw Failure(
                "QUERY_PUBLIC_LISTING_KIND_UNSUPPORTED",
                $"Catalog publication contains non-public subject kind '{source.SubjectKind}'.",
                "Publish only place or provider listings through the Catalog publication owner."),
        };
        var descriptions = source.Descriptions.ToDictionary(item => item.Locale, StringComparer.OrdinalIgnoreCase);
        var localizations = source.Names
            .Where(item => item.State == FieldValueStateContract.Observed)
            .Select(item =>
            {
                if (string.IsNullOrWhiteSpace(item.Value))
                {
                    throw Failure(
                        "QUERY_TITLE_INVALID",
                        $"Observed title for locale '{item.Locale}' is empty.",
                        "Correct and republish the Catalog listing revision.");
                }

                descriptions.TryGetValue(item.Locale, out var description);
                var descriptionState = description is null
                    ? QueryFieldState.Missing
                    : MapFieldState(description.State);
                var descriptionValue = descriptionState == QueryFieldState.Observed
                    ? description!.Value
                    : null;
                return new QueryLocalizedDocument(
                    item.Locale,
                    GetListingRoute(source.ListingId, item.Locale, listingRoutes),
                    item.Value,
                    descriptionState,
                    descriptionValue);
            })
            .ToArray();
        var attributes = source.Attributes.Select(MapAttribute).ToArray();
        var geography = new QueryGeographyDocument(
            source.Geography.State switch
            {
                GeographyStateContract.PrimaryMarket => QueryGeographyState.PrimaryMarket,
                GeographyStateContract.NearbyMarket => QueryGeographyState.NearbyMarket,
                GeographyStateContract.RemoteOnly => QueryGeographyState.RemoteOnly,
                GeographyStateContract.OutsideMarket => QueryGeographyState.OutsideMarket,
                _ => throw Failure(
                    "QUERY_GEOGRAPHY_UNRESOLVED",
                    $"Listing '{source.ListingId}' has unresolved geography.",
                    "Resolve geography in Catalog and create a new publication."),
            },
            source.Geography.Latitude,
            source.Geography.Longitude,
            source.Geography.DistrictKey);
        return QueryListingDocument.Create(
            source.ListingId,
            source.ListingRevisionId,
            source.SubjectId,
            source.SubjectRevisionId,
            listingKind,
            localizations,
            source.CategoryKeys,
            attributes,
            geography,
            source.Contacts.Select(item => new QueryContactDocument(
                item.ContactId,
                MapContactKind(item.Kind),
                item.Target,
                item.Label)),
            source.Media.Select(item => new QueryMediaDocument(
                item.MediaId,
                item.ObjectUri,
                item.ContentType,
                item.ContentDigest,
                MapMediaRightsBasis(item.RightsBasis))),
            source.ContentDigest,
            publishedAtUtc);
    }

    private static QueryAttributeDocument MapAttribute(PublicAttributeValue source)
    {
        var state = MapFieldState(source.State);
        if (state != QueryFieldState.Observed)
        {
            return new QueryAttributeDocument(source.AttributeKey, state, null, null, null, null, null);
        }

        var value = source.Value ?? throw Failure(
            "QUERY_ATTRIBUTE_VALUE_REQUIRED",
            $"Observed attribute '{source.AttributeKey}' has no typed value.",
            "Correct and republish the Catalog listing revision.");
        var kind = value.Kind switch
        {
            AttributeValueKindContract.Boolean => QueryValueKind.BooleanValue,
            AttributeValueKindContract.Decimal => QueryValueKind.DecimalNumber,
            AttributeValueKindContract.Text => QueryValueKind.TextValue,
            AttributeValueKindContract.TextSet => QueryValueKind.TextCollection,
            AttributeValueKindContract.DurationMinutes => QueryValueKind.DurationMinutes,
            _ => throw Failure(
                "QUERY_ATTRIBUTE_KIND_UNSUPPORTED",
                $"Attribute '{source.AttributeKey}' has unsupported value kind '{value.Kind}'.",
                "Upgrade Query to the exact Catalog contract before consuming this publication."),
        };
        return new QueryAttributeDocument(
            source.AttributeKey,
            state,
            kind,
            value.BooleanValue,
            value.DecimalValue,
            value.TextValue,
            value.TextSetValue);
    }

    private static QueryFieldState MapFieldState(FieldValueStateContract state) => state switch
    {
        FieldValueStateContract.Observed => QueryFieldState.Observed,
        FieldValueStateContract.Missing => QueryFieldState.Missing,
        FieldValueStateContract.NotApplicable => QueryFieldState.NotApplicable,
        FieldValueStateContract.Withheld => QueryFieldState.Withheld,
        _ => throw Failure(
            "QUERY_FIELD_STATE_UNSUPPORTED",
            $"Catalog field state '{state}' is unsupported.",
            "Upgrade Query to the exact Catalog contract before consuming this publication."),
    };

    private static QueryContactKind MapContactKind(ContactKindContract kind) => kind switch
    {
        ContactKindContract.Website => QueryContactKind.Website,
        ContactKindContract.Email => QueryContactKind.Email,
        ContactKindContract.Phone => QueryContactKind.Phone,
        ContactKindContract.WhatsApp => QueryContactKind.WhatsApp,
        ContactKindContract.BookingReference => QueryContactKind.BookingReference,
        ContactKindContract.MapReference => QueryContactKind.MapReference,
        _ => throw Failure(
            "QUERY_CONTACT_KIND_UNSUPPORTED",
            $"Catalog contact kind '{kind}' is unsupported.",
            "Upgrade Query to the exact Catalog contract before consuming this publication."),
    };

    private static QueryMediaRightsBasis MapMediaRightsBasis(MediaRightsBasisContract rightsBasis) => rightsBasis switch
    {
        MediaRightsBasisContract.OwnerProvided => QueryMediaRightsBasis.OwnerProvided,
        MediaRightsBasisContract.ExplicitLicense => QueryMediaRightsBasis.ExplicitLicense,
        MediaRightsBasisContract.OriginalEditorialWork => QueryMediaRightsBasis.OriginalEditorialWork,
        MediaRightsBasisContract.PublicDomain => QueryMediaRightsBasis.PublicDomain,
        _ => throw Failure(
            "QUERY_MEDIA_RIGHTS_BASIS_UNSUPPORTED",
            $"Catalog media rights basis '{rightsBasis}' is unsupported.",
            "Upgrade Query to the exact Catalog contract before consuming this publication."),
    };

    private static IReadOnlyDictionary<ListingRouteIdentity, PublicRouteDocument> BuildListingRouteMap(
        CatalogPublicationArtifact artifact,
        QueryLocalePolicy localePolicy)
    {
        var listingByGroup = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var listing in artifact.Listings)
        {
            var groupKey = CatalogPublicationRouteManifest.ListingGroupKey(listing.ListingId);
            if (!listingByGroup.TryAdd(groupKey, listing.ListingId))
            {
                throw Failure(
                    "QUERY_ROUTE_LISTING_DUPLICATE",
                    $"Catalog publication repeats listing '{listing.ListingId}'.",
                    "Correct and republish the exact Catalog publication artifact.");
            }
        }

        var routes = new Dictionary<ListingRouteIdentity, PublicRouteDocument>();
        foreach (var route in artifact.Routes.Where(route =>
                     route.RouteKind == CatalogPublicRouteKindContract.Listing))
        {
            if (!listingByGroup.TryGetValue(route.RouteGroupKey, out var listingId))
            {
                throw Failure(
                    "QUERY_ROUTE_LISTING_ORPHANED",
                    $"Listing route group '{route.RouteGroupKey}' has no listing in the exact publication.",
                    "Correct the Catalog route manifest and create a new publication.");
            }

            if (!localePolicy.Supports(route.Locale))
            {
                throw Failure(
                    "QUERY_ROUTE_LOCALE_UNSUPPORTED",
                    $"Route locale '{route.Locale}' is not supported by Catalog '{artifact.CatalogKey}'.",
                    "Correct the Catalog route manifest and create a new publication.");
            }

            var identity = new ListingRouteIdentity(listingId, route.Locale);
            if (!routes.TryAdd(identity, route))
            {
                throw Failure(
                    "QUERY_ROUTE_LISTING_LOCALE_DUPLICATE",
                    $"Listing '{listingId}' has multiple current routes for locale '{route.Locale}'.",
                    "Correct the Catalog route manifest and create a new publication.");
            }
        }

        foreach (var listing in artifact.Listings)
        {
            var observedLocales = listing.Names
                .Where(name => name.State == FieldValueStateContract.Observed)
                .Select(name => name.Locale)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var locale in observedLocales)
            {
                if (!routes.ContainsKey(new ListingRouteIdentity(listing.ListingId, locale)))
                {
                    throw Failure(
                        "QUERY_ROUTE_LISTING_MISSING",
                        $"Listing '{listing.ListingId}' has no current route for observed locale '{locale}'.",
                        "Correct the Catalog route manifest and create a new publication.");
                }
            }

            var unexpectedRoute = routes
                .Where(pair => pair.Key.ListingId == listing.ListingId)
                .Select(pair => pair.Key.Locale)
                .FirstOrDefault(locale => !observedLocales.Contains(locale));
            if (unexpectedRoute is not null)
            {
                throw Failure(
                    "QUERY_ROUTE_LISTING_LOCALE_UNPUBLISHED",
                    $"Listing '{listing.ListingId}' route locale '{unexpectedRoute}' has no observed public title.",
                    "Correct the Catalog route manifest and create a new publication.");
            }
        }

        return routes;
    }

    private static string GetListingRoute(
        Guid listingId,
        string locale,
        IReadOnlyDictionary<ListingRouteIdentity, PublicRouteDocument> routes)
    {
        if (!routes.TryGetValue(new ListingRouteIdentity(listingId, locale), out var route))
        {
            throw Failure(
                "QUERY_ROUTE_LISTING_MISSING",
                $"Listing '{listingId}' has no current route for observed locale '{locale}'.",
                "Correct the Catalog route manifest and create a new publication.");
        }

        return route.Path;
    }

    private static IReadOnlyList<PublicSeoRouteSource> BuildSeoRouteSources(
        CatalogPublicationArtifact artifact,
        QueryLocalePolicy localePolicy)
    {
        var sources = new List<PublicSeoRouteSource>(
            artifact.Routes.Count + artifact.Redirects.Count);
        foreach (var route in artifact.Routes)
        {
            EnsureSupportedRouteLocale(artifact.CatalogKey, localePolicy, route.Locale);
            sources.Add(new PublicSeoRouteSource(
                MapRouteKind(route.RouteKind),
                route.RouteGroupKey,
                artifact.CatalogKey,
                route.Locale,
                route.Path,
                route.LastModifiedAtUtc,
                route.IsDraft,
                RedirectTargetPath: null,
                IsSuppressed: route.IsSuppressed));
        }

        foreach (var redirect in artifact.Redirects)
        {
            EnsureSupportedRouteLocale(artifact.CatalogKey, localePolicy, redirect.Locale);
            sources.Add(new PublicSeoRouteSource(
                MapRouteKind(redirect.RouteKind),
                redirect.RouteGroupKey,
                artifact.CatalogKey,
                redirect.Locale,
                redirect.SourcePath,
                redirect.CreatedAtUtc,
                IsDraft: false,
                RedirectTargetPath: redirect.TargetPath,
                IsSuppressed: false,
                RedirectSourcePublicationId: redirect.SourcePublicationId,
                RedirectReason: redirect.Reason,
                RedirectCreatedAtUtc: redirect.CreatedAtUtc));
        }

        return sources;
    }

    private static void EnsureSupportedRouteLocale(
        string catalogKey,
        QueryLocalePolicy localePolicy,
        string locale)
    {
        if (!localePolicy.Supports(locale))
        {
            throw Failure(
                "QUERY_ROUTE_LOCALE_UNSUPPORTED",
                $"Route locale '{locale}' is not supported by Catalog '{catalogKey}'.",
                "Correct the Catalog route manifest and create a new publication.");
        }
    }

    private static QuerySeoRouteKind MapRouteKind(CatalogPublicRouteKindContract routeKind) =>
        routeKind switch
        {
            CatalogPublicRouteKindContract.Listing => QuerySeoRouteKind.Listing,
            CatalogPublicRouteKindContract.Category => QuerySeoRouteKind.Category,
            CatalogPublicRouteKindContract.EditorialLanding => QuerySeoRouteKind.EditorialLanding,
            _ => throw Failure(
                "QUERY_ROUTE_KIND_UNSUPPORTED",
                $"Catalog route kind '{routeKind}' is unsupported.",
                "Upgrade Query to the exact Catalog route contract before consuming this publication."),
        };

    private sealed record ListingRouteIdentity(Guid ListingId, string Locale);

    private static void ValidateSourceIdentity(
        CatalogPublicationActivated activation,
        CatalogPublicationArtifact artifact)
    {
        if (!string.Equals(artifact.ContractIdentity, CatalogPublicationArtifactContract.Identity, StringComparison.Ordinal) ||
            artifact.ContractRevision != CatalogPublicationArtifactContract.Revision)
        {
            throw Failure(
                "QUERY_PUBLICATION_CONTRACT_UNSUPPORTED",
                $"Catalog publication artifact contract '{artifact.ContractIdentity}' revision '{artifact.ContractRevision}' is unsupported.",
                "Publish an artifact using the exact Catalog contract supported by Query.");
        }

        if (artifact.PublicationId != activation.PublicationId ||
            !string.Equals(artifact.CatalogKey, activation.CatalogKey, StringComparison.Ordinal) ||
            artifact.ConfigurationRevisionId != activation.ConfigurationRevisionId ||
            artifact.PublicationSequence != activation.PublicationSequence)
        {
            throw Failure(
                "QUERY_PUBLICATION_IDENTITY_MISMATCH",
                "Catalog publication event and artifact identities do not match.",
                "Inspect the Catalog outbox event and sealed artifact; do not activate either until the mismatch is corrected.");
        }
    }

    private static QueryProjectionException Failure(string code, string message, string requiredAction) =>
        new(
            "Query.ProjectionBuild",
            code,
            422,
            message,
            requiredAction);
}
