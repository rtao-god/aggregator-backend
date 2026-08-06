using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

internal static class CatalogPublicationArtifactFactory
{
    public static CatalogPublicationArtifact Create(
        Guid publicationId,
        ProductConfiguration configuration,
        long sequence,
        DateTimeOffset createdAtUtc,
        IReadOnlyList<PublicationSelectionState> selections)
    {
        if (publicationId == Guid.Empty)
        {
            throw new ArgumentException("Publication ID is required.", nameof(publicationId));
        }

        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selections);

        var listings = selections
            .OrderBy(selection => selection.Listing.Id)
            .Select(selection => CreateDocument(selection.Revision))
            .ToArray();
        var supportedLocales = configuration.Site.SupportedLocales
            .Select(locale => locale.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new CatalogPublicationArtifact(
            CatalogPublicationArtifactContract.Identity,
            CatalogPublicationArtifactContract.Revision,
            publicationId,
            configuration.Catalog.Key.Value,
            configuration.Site.DefaultLocale.Value,
            supportedLocales,
            configuration.RevisionId,
            sequence,
            createdAtUtc,
            listings);
    }

    private static PublicListingDocument CreateDocument(ListingRevision revision)
    {
        var content = revision.Content;
        return new PublicListingDocument(
            revision.ListingId,
            revision.Id,
            revision.Subject.SubjectId,
            revision.Subject.SubjectRevisionId,
            (SubjectKindContract)revision.Subject.Kind,
            content.Names.Values
                .OrderBy(value => value.Locale.Value, StringComparer.Ordinal)
                .Select(value => new PublicLocalizedText(
                    value.Locale.Value,
                    (FieldValueStateContract)value.State,
                    value.Value,
                    value.MissingReason is null ? null : (MissingValueReasonContract)value.MissingReason,
                    value.AssertionId))
                .ToArray(),
            content.Descriptions.Values
                .OrderBy(value => value.Locale.Value, StringComparer.Ordinal)
                .Select(value => new PublicLocalizedText(
                    value.Locale.Value,
                    (FieldValueStateContract)value.State,
                    value.Value,
                    value.MissingReason is null ? null : (MissingValueReasonContract)value.MissingReason,
                    value.AssertionId))
                .ToArray(),
            content.Categories.Select(category => category.CategoryKey.Value).Order(StringComparer.Ordinal).ToArray(),
            content.Attributes.Values
                .OrderBy(value => value.Key.Value, StringComparer.Ordinal)
                .Select(value => new PublicAttributeValue(
                    value.Key.Value,
                    (FieldValueStateContract)value.State,
                    value.Value is null
                        ? null
                        : new TypedValueContract(
                            (AttributeValueKindContract)value.Value.Kind,
                            value.Value.BooleanValue,
                            value.Value.DecimalValue,
                            value.Value.TextValue,
                            value.Value.TextSetValue),
                    value.MissingReason is null ? null : (MissingValueReasonContract)value.MissingReason,
                    value.AssertionId))
                .ToArray(),
            new PublicGeography(
                (GeographyStateContract)content.Geography.State,
                content.Geography.Latitude,
                content.Geography.Longitude,
                content.Geography.DistrictKey,
                content.Geography.AssertionId),
            content.Contacts
                .OrderBy(contact => contact.Kind)
                .ThenBy(contact => contact.Target.AbsoluteUri, StringComparer.Ordinal)
                .ThenBy(contact => contact.Id)
                .Select(contact => new PublicContact(
                    contact.Id,
                    (ContactKindContract)contact.Kind,
                    contact.Target.AbsoluteUri,
                    contact.Label,
                    contact.AssertionId))
                .ToArray(),
            content.Media
                .OrderBy(media => media.MediaId)
                .Select(media => new PublicMedia(
                    media.MediaId,
                    media.ObjectUri.AbsoluteUri,
                    media.ContentType,
                    media.ContentDigest,
                    (MediaRightsBasisContract)media.RightsBasis,
                    media.AssertionId))
                .ToArray(),
            content.Assertions.Values
                .OrderBy(assertion => assertion.Id)
                .Select(assertion => new PublicProvenanceSummary(
                    assertion.Id,
                    (SourceKindContract)assertion.SourceKind,
                    assertion.ObservedAtUtc,
                    assertion.EvidenceDigest))
                .ToArray(),
            revision.ContentDigest);
    }
}
