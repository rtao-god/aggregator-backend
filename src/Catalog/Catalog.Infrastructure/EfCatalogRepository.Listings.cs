using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository
{
    public async Task AddListingAsync(Listing listing, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listing);
        var snapshot = listing.ToSnapshot();
        _dbContext.Listings.Add(ToRow(snapshot));
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            throw new CatalogConflictException(
                $"Listing '{listing.Id}' or subject '{listing.Subject.SubjectId}' already exists in catalog '{listing.CatalogKey}'.")
            {
                Source = exception.Source,
            };
        }
    }

    public async Task<Listing?> GetListingAsync(Guid listingId, CancellationToken cancellationToken)
    {
        var row = await _dbContext.Listings
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == listingId, cancellationToken);
        return row is null ? null : RehydrateListing(row);
    }

    public async Task<ListingRevision?> GetListingRevisionAsync(
        Guid revisionId,
        CancellationToken cancellationToken)
    {
        var revisionRow = await _dbContext.ListingRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == revisionId, cancellationToken);
        if (revisionRow is null)
        {
            return null;
        }

        var configuration = await GetConfigurationAsync(
                revisionRow.ConfigurationRevisionId,
                cancellationToken)
            ?? throw new InvalidOperationException(
                $"Listing revision '{revisionId}' references missing configuration '{revisionRow.ConfigurationRevisionId}'.");
        var assertions = await _dbContext.ProvenanceAssertions
            .AsNoTracking()
            .Where(row => row.ListingRevisionId == revisionId)
            .OrderBy(row => row.AssertionId)
            .ToArrayAsync(cancellationToken);
        var localizedTexts = await _dbContext.LocalizedTexts
            .AsNoTracking()
            .Where(row => row.ListingRevisionId == revisionId)
            .OrderBy(row => row.FieldKind)
            .ThenBy(row => row.Locale)
            .ToArrayAsync(cancellationToken);
        var categories = await _dbContext.CategoryAssignments
            .AsNoTracking()
            .Where(row => row.ListingRevisionId == revisionId)
            .OrderBy(row => row.CategoryKey)
            .ToArrayAsync(cancellationToken);
        var attributes = await _dbContext.AttributeValues
            .AsNoTracking()
            .Where(row => row.ListingRevisionId == revisionId)
            .OrderBy(row => row.AttributeKey)
            .ToArrayAsync(cancellationToken);
        var geography = await _dbContext.Geographies
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.ListingRevisionId == revisionId, cancellationToken)
            ?? throw new InvalidOperationException($"Listing revision '{revisionId}' has no geography row.");
        var contacts = await _dbContext.Contacts
            .AsNoTracking()
            .Where(row => row.ListingRevisionId == revisionId)
            .OrderBy(row => row.Kind)
            .ThenBy(row => row.Target)
            .ToArrayAsync(cancellationToken);
        var media = await _dbContext.Media
            .AsNoTracking()
            .Where(row => row.ListingRevisionId == revisionId)
            .OrderBy(row => row.MediaId)
            .ToArrayAsync(cancellationToken);

        var subjectKind = RequireEnum<SubjectKind>(revisionRow.SubjectKind, "subject kind");
        var content = ListingRevisionContent.Create(
            subjectKind,
            localizedTexts.Where(row => row.FieldKind == "name").Select(RehydrateLocalizedText),
            localizedTexts.Where(row => row.FieldKind == "description").Select(RehydrateLocalizedText),
            categories.Select(row => CategoryAssignment.Create(
                CategoryKey.Create(row.CategoryKey),
                row.AssertionId)),
            attributes.Select(RehydrateAttribute),
            GeographyValue.Create(
                RequireEnum<GeographyState>(geography.State, "geography state"),
                geography.Latitude,
                geography.Longitude,
                geography.DistrictKey,
                geography.AssertionId),
            contacts.Select(row => ContactValue.Create(
                row.Id,
                RequireEnum<ContactKind>(row.Kind, "contact kind"),
                RequireAbsoluteUri(row.Target, "contact target"),
                row.Label,
                row.AssertionId)),
            media.Select(row => MediaReference.Create(
                row.MediaId,
                RequireAbsoluteUri(row.ObjectUri, "media object URI"),
                row.ContentType,
                row.ContentDigest,
                RequireEnum<MediaRightsBasis>(row.RightsBasis, "media rights basis"),
                row.RightsReference,
                row.AssertionId)),
            assertions.Select(row => ProvenanceAssertion.Create(
                row.AssertionId,
                RequireEnum<SourceKind>(row.SourceKind, "source kind"),
                row.SourceReference,
                row.ObservedAtUtc,
                row.RecordedAtUtc,
                RequireEnum<UsagePolicy>(row.UsagePolicy, "usage policy"),
                row.EvidenceDigest)),
            configuration);

        return ListingRevision.Create(
            revisionRow.Id,
            revisionRow.ListingId,
            revisionRow.RevisionNumber,
            revisionRow.ConfigurationRevisionId,
            SubjectReference.Create(
                revisionRow.SubjectId,
                revisionRow.SubjectRevisionId,
                subjectKind),
            content,
            revisionRow.ContentDigest,
            revisionRow.CreatedByActorId,
            revisionRow.CreatedAtUtc);
    }

    public Task AddListingRevisionAsync(
        Listing listing,
        ListingRevision revision,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            var row = await RequireTrackedListingAsync(listing.Id, innerCancellationToken);
            ApplyListingMutation(row, listing);
            _dbContext.ListingRevisions.Add(new CatalogListingRevisionRow
            {
                Id = revision.Id,
                ListingId = revision.ListingId,
                RevisionNumber = revision.RevisionNumber,
                ConfigurationRevisionId = revision.ConfigurationRevisionId,
                SubjectId = revision.Subject.SubjectId,
                SubjectRevisionId = revision.Subject.SubjectRevisionId,
                SubjectKind = (int)revision.Subject.Kind,
                ContentDigest = revision.ContentDigest,
                CreatedByActorId = revision.CreatedByActorId,
                CreatedAtUtc = revision.CreatedAtUtc,
            });
            AddRevisionContentRows(revision);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    public Task AddEditorialDecisionAsync(
        Listing listing,
        EditorialDecision decision,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            var row = await RequireTrackedListingAsync(listing.Id, innerCancellationToken);
            ApplyListingMutation(row, listing);
            _dbContext.EditorialDecisions.Add(new CatalogEditorialDecisionRow
            {
                Id = decision.Id,
                ListingId = decision.ListingId,
                RevisionId = decision.RevisionId,
                Kind = (int)decision.Kind,
                ActorId = decision.ActorId,
                Reason = decision.Reason,
                DecidedAtUtc = decision.DecidedAtUtc,
            });
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    public Task ArchiveListingAsync(Listing listing, CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            var row = await RequireTrackedListingAsync(listing.Id, innerCancellationToken);
            ApplyListingMutation(row, listing);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    public async Task<IReadOnlyList<PublicationSelectionState>> GetPublicationSelectionsAsync(
        CatalogKey catalogKey,
        IReadOnlyList<PublicationSelectionContract> selections,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        ArgumentNullException.ThrowIfNull(selections);
        var result = new List<PublicationSelectionState>(selections.Count);
        foreach (var selection in selections)
        {
            var listing = await GetListingAsync(selection.ListingId, cancellationToken)
                ?? throw new CatalogNotFoundException("listing", selection.ListingId);
            if (listing.CatalogKey != catalogKey)
            {
                throw new CatalogConflictException(
                    $"Listing '{listing.Id}' belongs to catalog '{listing.CatalogKey}', not '{catalogKey}'.");
            }

            var revision = await GetListingRevisionAsync(
                    selection.ListingRevisionId,
                    cancellationToken)
                ?? throw new CatalogNotFoundException(
                    "listing-revision",
                    selection.ListingRevisionId);
            if (revision.ListingId != listing.Id)
            {
                throw new CatalogConflictException(
                    $"Revision '{revision.Id}' does not belong to listing '{listing.Id}'.");
            }

            result.Add(new PublicationSelectionState(listing, revision));
        }

        return result;
    }

    private void AddRevisionContentRows(ListingRevision revision)
    {
        foreach (var assertion in revision.Content.Assertions.Values)
        {
            _dbContext.ProvenanceAssertions.Add(new CatalogProvenanceAssertionRow
            {
                ListingRevisionId = revision.Id,
                AssertionId = assertion.Id,
                SourceKind = (int)assertion.SourceKind,
                SourceReference = assertion.SourceReference,
                ObservedAtUtc = assertion.ObservedAtUtc,
                RecordedAtUtc = assertion.RecordedAtUtc,
                UsagePolicy = (int)assertion.UsagePolicy,
                EvidenceDigest = assertion.EvidenceDigest,
            });
        }

        AddLocalizedTextRows(revision.Id, "name", revision.Content.Names.Values);
        AddLocalizedTextRows(revision.Id, "description", revision.Content.Descriptions.Values);
        foreach (var category in revision.Content.Categories)
        {
            _dbContext.CategoryAssignments.Add(new CatalogCategoryAssignmentRow
            {
                ListingRevisionId = revision.Id,
                CategoryKey = category.CategoryKey.Value,
                AssertionId = category.AssertionId,
            });
        }

        foreach (var attribute in revision.Content.Attributes.Values)
        {
            _dbContext.AttributeValues.Add(new CatalogAttributeValueRow
            {
                ListingRevisionId = revision.Id,
                AttributeKey = attribute.Key.Value,
                State = (int)attribute.State,
                ValueKind = attribute.Value is null ? null : (int)attribute.Value.Kind,
                BooleanValue = attribute.Value?.BooleanValue,
                DecimalValue = attribute.Value?.DecimalValue,
                TextValue = attribute.Value?.TextValue,
                TextSetValue = attribute.Value?.TextSetValue?.ToArray(),
                AssertionId = attribute.AssertionId,
                MissingReason = attribute.MissingReason is null ? null : (int)attribute.MissingReason,
            });
        }

        _dbContext.Geographies.Add(new CatalogGeographyRow
        {
            ListingRevisionId = revision.Id,
            State = (int)revision.Content.Geography.State,
            Latitude = revision.Content.Geography.Latitude,
            Longitude = revision.Content.Geography.Longitude,
            DistrictKey = revision.Content.Geography.DistrictKey,
            AssertionId = revision.Content.Geography.AssertionId,
        });

        foreach (var contact in revision.Content.Contacts)
        {
            _dbContext.Contacts.Add(new CatalogContactRow
            {
                Id = contact.Id,
                ListingRevisionId = revision.Id,
                Kind = (int)contact.Kind,
                Target = contact.Target.AbsoluteUri,
                Label = contact.Label,
                AssertionId = contact.AssertionId,
            });
        }

        foreach (var media in revision.Content.Media)
        {
            _dbContext.Media.Add(new CatalogMediaRow
            {
                MediaId = media.MediaId,
                ListingRevisionId = revision.Id,
                ObjectUri = media.ObjectUri.AbsoluteUri,
                ContentType = media.ContentType,
                ContentDigest = media.ContentDigest,
                RightsBasis = (int)media.RightsBasis,
                RightsReference = media.RightsReference,
                AssertionId = media.AssertionId,
            });
        }
    }

    private void AddLocalizedTextRows(
        Guid revisionId,
        string fieldKind,
        IEnumerable<LocalizedTextValue> values)
    {
        foreach (var value in values)
        {
            _dbContext.LocalizedTexts.Add(new CatalogLocalizedTextRow
            {
                ListingRevisionId = revisionId,
                FieldKind = fieldKind,
                Locale = value.Locale.Value,
                State = (int)value.State,
                TextValue = value.Value,
                AssertionId = value.AssertionId,
                MissingReason = value.MissingReason is null ? null : (int)value.MissingReason,
            });
        }
    }

    private async Task<CatalogListingRow> RequireTrackedListingAsync(
        Guid listingId,
        CancellationToken cancellationToken) =>
        await _dbContext.Listings.SingleOrDefaultAsync(
                row => row.Id == listingId,
                cancellationToken)
            ?? throw new CatalogNotFoundException("listing", listingId);

    private static void ApplyListingMutation(CatalogListingRow row, Listing listing)
    {
        var snapshot = listing.ToSnapshot();
        var expectedPreviousVersion = checked(snapshot.Version - 1);
        if (row.Version != expectedPreviousVersion)
        {
            throw new CatalogConcurrencyException(listing.Id, expectedPreviousVersion, row.Version);
        }

        row.SubjectRevisionId = snapshot.SubjectRevisionId;
        row.SubjectKind = (int)snapshot.SubjectKind;
        row.State = (int)snapshot.State;
        row.Version = snapshot.Version;
        row.LatestRevisionNumber = snapshot.LatestRevisionNumber;
        row.CurrentDraftRevisionId = snapshot.CurrentDraftRevisionId;
        row.ApprovedRevisionId = snapshot.ApprovedRevisionId;
        row.PublishedRevisionId = snapshot.PublishedRevisionId;
        row.UpdatedAtUtc = snapshot.UpdatedAtUtc;
    }

    private static CatalogListingRow ToRow(ListingSnapshot snapshot) =>
        new()
        {
            Id = snapshot.Id,
            CatalogKey = snapshot.CatalogKey,
            SubjectId = snapshot.SubjectId,
            SubjectRevisionId = snapshot.SubjectRevisionId,
            SubjectKind = (int)snapshot.SubjectKind,
            State = (int)snapshot.State,
            Version = snapshot.Version,
            LatestRevisionNumber = snapshot.LatestRevisionNumber,
            CurrentDraftRevisionId = snapshot.CurrentDraftRevisionId,
            ApprovedRevisionId = snapshot.ApprovedRevisionId,
            PublishedRevisionId = snapshot.PublishedRevisionId,
            CreatedAtUtc = snapshot.CreatedAtUtc,
            UpdatedAtUtc = snapshot.UpdatedAtUtc,
        };

    private static Listing RehydrateListing(CatalogListingRow row) =>
        Listing.Restore(new ListingSnapshot(
            row.Id,
            row.CatalogKey,
            row.SubjectId,
            row.SubjectRevisionId,
            RequireEnum<SubjectKind>(row.SubjectKind, "subject kind"),
            RequireEnum<ListingLifecycleState>(row.State, "listing state"),
            row.Version,
            row.LatestRevisionNumber,
            row.CurrentDraftRevisionId,
            row.ApprovedRevisionId,
            row.PublishedRevisionId,
            row.CreatedAtUtc,
            row.UpdatedAtUtc));

    private static LocalizedTextValue RehydrateLocalizedText(CatalogLocalizedTextRow row)
    {
        var locale = LocaleCode.Create(row.Locale);
        var state = RequireEnum<FieldValueState>(row.State, "localized text state");
        return state switch
        {
            FieldValueState.Observed => LocalizedTextValue.Observed(
                locale,
                row.TextValue ?? throw new InvalidOperationException("Observed localized text row has no value."),
                row.AssertionId ?? throw new InvalidOperationException("Observed localized text row has no assertion.")),
            FieldValueState.Missing => LocalizedTextValue.Missing(
                locale,
                RequireNullableEnum<MissingValueReason>(row.MissingReason, "missing reason")),
            FieldValueState.Withheld => LocalizedTextValue.Withheld(
                locale,
                RequireNullableEnum<MissingValueReason>(row.MissingReason, "withheld reason")),
            _ => throw new InvalidOperationException($"Localized text row has unsupported state '{state}'."),
        };
    }

    private static ListingAttributeValue RehydrateAttribute(CatalogAttributeValueRow row)
    {
        var key = AttributeKey.Create(row.AttributeKey);
        var state = RequireEnum<FieldValueState>(row.State, "attribute state");
        return state switch
        {
            FieldValueState.Observed => ListingAttributeValue.Observed(
                key,
                RehydrateTypedValue(row),
                row.AssertionId ?? throw new InvalidOperationException("Observed attribute row has no assertion.")),
            FieldValueState.Missing => ListingAttributeValue.Missing(
                key,
                RequireNullableEnum<MissingValueReason>(row.MissingReason, "missing reason")),
            FieldValueState.NotApplicable => ListingAttributeValue.NotApplicable(key),
            _ => throw new InvalidOperationException($"Attribute row has unsupported state '{state}'."),
        };
    }

    private static TypedValue RehydrateTypedValue(CatalogAttributeValueRow row)
    {
        var kind = RequireNullableEnum<AttributeValueKind>(row.ValueKind, "attribute value kind");
        return kind switch
        {
            AttributeValueKind.Boolean => TypedValue.Boolean(
                row.BooleanValue ?? throw new InvalidOperationException("Boolean attribute row has no value.")),
            AttributeValueKind.Decimal => TypedValue.Decimal(
                row.DecimalValue ?? throw new InvalidOperationException("Decimal attribute row has no value.")),
            AttributeValueKind.DurationMinutes => TypedValue.DurationMinutes(
                decimal.ToInt32(row.DecimalValue ?? throw new InvalidOperationException("Duration attribute row has no value."))),
            AttributeValueKind.Text => TypedValue.Text(
                row.TextValue ?? throw new InvalidOperationException("Text attribute row has no value.")),
            AttributeValueKind.TextSet => TypedValue.TextSet(
                row.TextSetValue ?? throw new InvalidOperationException("Text-set attribute row has no value.")),
            _ => throw new InvalidOperationException($"Unsupported attribute value kind '{kind}'."),
        };
    }

    private static TEnum RequireEnum<TEnum>(int value, string fieldName)
        where TEnum : struct, Enum
    {
        var enumValue = (TEnum)Enum.ToObject(typeof(TEnum), value);
        if (!Enum.IsDefined(enumValue))
        {
            throw new InvalidOperationException($"Persisted {fieldName} value '{value}' is invalid.");
        }

        return enumValue;
    }

    private static TEnum RequireNullableEnum<TEnum>(int? value, string fieldName)
        where TEnum : struct, Enum =>
        value is null
            ? throw new InvalidOperationException($"Persisted {fieldName} is missing.")
            : RequireEnum<TEnum>(value.Value, fieldName);

    private static Uri RequireAbsoluteUri(string value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Persisted {fieldName} '{value}' is not an absolute URI.");
        }

        return uri;
    }
}
