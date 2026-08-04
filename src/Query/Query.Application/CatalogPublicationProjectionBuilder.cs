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
        var documents = artifact.Listings
            .OrderBy(item => item.ListingId)
            .Select(item => MapDocument(item, artifact.CreatedAtUtc))
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
        var publicReadDigest = QueryCanonicalJson.ComputeDigest(new
        {
            baseProjectionDigest = baseProjection.ContentDigest,
            promotionOverlayDigest = promotionOverlay.ContentDigest,
            safetyOverlayDigest = safetyOverlay.ContentDigest,
            artifact.PublicationId,
        });
        var publicReadRevision = PublicReadRevision.Create(
            publicReadRevisionId,
            baseProjection,
            promotionOverlay,
            safetyOverlay,
            builtAtUtc,
            publicReadDigest);
        return new QueryProjectionActivation(baseProjection, promotionOverlay, safetyOverlay, publicReadRevision);
    }

    private static QueryListingDocument MapDocument(PublicListingDocument source, DateTimeOffset publishedAtUtc)
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
                    BuildRoute(item.Locale, source.ListingId),
                    item.Value,
                    descriptionState,
                    descriptionValue);
            })
            .ToArray();
        var attributes = source.Attributes.Select(MapAttribute).ToArray();
        var geography = new QueryGeographyDocument(
            source.Geography.State switch
            {
                GeographyStateContract.BerlinCore => QueryGeographyState.PrimaryMarket,
                GeographyStateContract.BerlinNearby => QueryGeographyState.NearbyMarket,
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

    private static string BuildRoute(string locale, Guid listingId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        return $"/{locale}/listings/{listingId:N}";
    }

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
