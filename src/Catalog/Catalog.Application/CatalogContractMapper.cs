using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

internal static class CatalogContractMapper
{
    public static ProductConfiguration ToDomain(
        ProductConfigurationContract contract,
        string contentDigest)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(contract.Site);
        ArgumentNullException.ThrowIfNull(contract.Catalog);
        ArgumentNullException.ThrowIfNull(contract.Categories);
        ArgumentNullException.ThrowIfNull(contract.Attributes);
        ArgumentNullException.ThrowIfNull(contract.Site.SupportedLocales);
        ArgumentNullException.ThrowIfNull(contract.Catalog.AllowedListingKinds);

        var site = SiteDefinition.Create(
            SiteKey.Create(contract.Site.Key),
            LocaleCode.Create(contract.Site.DefaultLocale),
            contract.Site.SupportedLocales.Select(LocaleCode.Create),
            contract.Site.Currency,
            contract.Site.TimeZone);
        var catalog = CatalogDefinition.Create(
            CatalogKey.Create(contract.Catalog.Key),
            SiteKey.Create(contract.Catalog.SiteKey),
            contract.Catalog.MarketAreaKey,
            contract.Catalog.Currency,
            contract.Catalog.TimeZone,
            contract.Catalog.AllowedListingKinds.Select(ToDomain));
        var categories = contract.Categories.Select(ToDomain).ToArray();
        var attributes = contract.Attributes.Select(ToDomain).ToArray();

        return ProductConfiguration.Create(
            contract.RevisionId,
            contentDigest,
            site,
            catalog,
            categories,
            attributes,
            contract.CreatedAtUtc);
    }

    public static SubjectReference ToDomain(SubjectReferenceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return SubjectReference.Create(
            contract.SubjectId,
            contract.SubjectRevisionId,
            ToDomain(contract.Kind));
    }

    public static ListingRevisionContent ToDomain(
        SubjectKind subjectKind,
        ListingRevisionContentContract contract,
        ProductConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(contract.Names);
        ArgumentNullException.ThrowIfNull(contract.Descriptions);
        ArgumentNullException.ThrowIfNull(contract.Categories);
        ArgumentNullException.ThrowIfNull(contract.Attributes);
        ArgumentNullException.ThrowIfNull(contract.Geography);
        ArgumentNullException.ThrowIfNull(contract.Contacts);
        ArgumentNullException.ThrowIfNull(contract.Media);
        ArgumentNullException.ThrowIfNull(contract.Assertions);

        var assertions = contract.Assertions.Select(ToDomain).ToArray();
        var names = contract.Names.Select(ToDomainLocalizedText).ToArray();
        var descriptions = contract.Descriptions.Select(ToDomainLocalizedText).ToArray();
        var categories = contract.Categories.Select(ToDomain).ToArray();
        var attributes = contract.Attributes.Select(ToDomain).ToArray();
        var geography = GeographyValue.Create(
            ToDomain(contract.Geography.State),
            contract.Geography.Latitude,
            contract.Geography.Longitude,
            contract.Geography.DistrictKey,
            contract.Geography.AssertionId);
        var contacts = contract.Contacts.Select(ToDomain).ToArray();
        var media = contract.Media.Select(ToDomain).ToArray();

        return ListingRevisionContent.Create(
            subjectKind,
            names,
            descriptions,
            categories,
            attributes,
            geography,
            contacts,
            media,
            assertions,
            configuration);
    }

    public static ListingResponse ToResponse(Listing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return new ListingResponse(
            listing.Id,
            listing.CatalogKey.Value,
            ToContract(listing.Subject),
            ToContract(listing.State),
            listing.Version,
            listing.LatestRevisionNumber,
            listing.CurrentDraftRevisionId,
            listing.ApprovedRevisionId,
            listing.PublishedRevisionId,
            listing.CreatedAtUtc,
            listing.UpdatedAtUtc);
    }

    public static ListingRevisionResponse ToResponse(ListingRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new ListingRevisionResponse(
            revision.Id,
            revision.ListingId,
            revision.RevisionNumber,
            revision.ConfigurationRevisionId,
            ToContract(revision.Subject),
            revision.ContentDigest,
            revision.CreatedAtUtc);
    }

    public static EditorialDecisionResponse ToResponse(EditorialDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var decisionName = decision.Kind switch
        {
            EditorialDecisionKind.Approved => "approved",
            EditorialDecisionKind.Rejected => "rejected",
            _ => throw UnsupportedDomainEnum(nameof(EditorialDecisionKind), decision.Kind),
        };
        return new EditorialDecisionResponse(
            decision.Id,
            decision.ListingId,
            decision.RevisionId,
            decisionName,
            decision.Reason,
            decision.DecidedAtUtc);
    }

    public static CatalogPublicationResponse ToResponse(
        CatalogPublication publication,
        bool isCurrent)
    {
        ArgumentNullException.ThrowIfNull(publication);
        return new CatalogPublicationResponse(
            publication.Id,
            publication.CatalogKey.Value,
            publication.ConfigurationRevisionId,
            publication.Sequence,
            publication.ArtifactKey,
            publication.ArtifactDigest,
            publication.Entries
                .OrderBy(entry => entry.ListingId)
                .Select(entry => new PublicationEntryContract(
                    entry.ListingId,
                    entry.ListingRevisionId,
                    entry.SubjectRevisionId,
                    entry.ContentDigest))
                .ToArray(),
            publication.CreatedAtUtc,
            isCurrent);
    }

    public static ListingClaimResponse ToResponse(ListingClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        return new ListingClaimResponse(
            claim.Id,
            claim.ListingId,
            claim.ClaimantActorId,
            ToContract(claim.State),
            claim.EvidenceReference,
            claim.EvidenceDigest,
            claim.SubmittedAtUtc,
            claim.DecidedByActorId,
            claim.DecidedAtUtc,
            claim.DecisionReason);
    }

    public static ListingAccessGrantResponse ToResponse(ListingAccessGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        return new ListingAccessGrantResponse(
            grant.Id,
            grant.ListingId,
            grant.ActorId,
            grant.Scopes.Select(ToContract).OrderBy(scope => (int)scope).ToArray(),
            grant.GrantedAtUtc,
            grant.ExpiresAtUtc,
            grant.ClaimId,
            grant.RevokedAtUtc);
    }

    public static ListingAccessScope ToDomain(ListingAccessScopeContract value) => value switch
    {
        ListingAccessScopeContract.ReadDraft => ListingAccessScope.ReadDraft,
        ListingAccessScopeContract.ProposeRevision => ListingAccessScope.ProposeRevision,
        ListingAccessScopeContract.ManageContacts => ListingAccessScope.ManageContacts,
        ListingAccessScopeContract.ManageMedia => ListingAccessScope.ManageMedia,
        _ => throw UnsupportedContractEnum(nameof(ListingAccessScopeContract), value),
    };

    private static CategoryDefinition ToDomain(CategoryDefinitionContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(contract.SubjectKinds);
        ArgumentNullException.ThrowIfNull(contract.LocalizedNames);
        return CategoryDefinition.Create(
            CategoryKey.Create(contract.Key),
            contract.SubjectKinds.Select(ToDomain),
            contract.LocalizedNames.ToDictionary(
                item => LocaleCode.Create(item.Key),
                item => item.Value),
            contract.IsActive);
    }

    private static AttributeDefinition ToDomain(AttributeDefinitionContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(contract.Categories);
        ArgumentNullException.ThrowIfNull(contract.LocalizedNames);
        ArgumentNullException.ThrowIfNull(contract.AllowedValues);
        return AttributeDefinition.Create(
            AttributeKey.Create(contract.Key),
            ToDomain(contract.ValueKind),
            ToDomain(contract.Cardinality),
            ToDomain(contract.Requirement),
            contract.Categories.Select(CategoryKey.Create),
            contract.LocalizedNames.ToDictionary(
                item => LocaleCode.Create(item.Key),
                item => item.Value),
            contract.Minimum,
            contract.Maximum,
            contract.AllowedValues,
            contract.IsFilterable,
            contract.IsSortable);
    }

    private static ProvenanceAssertion ToDomain(ProvenanceAssertionContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return ProvenanceAssertion.Create(
            contract.Id,
            ToDomain(contract.SourceKind),
            contract.SourceReference,
            contract.ObservedAtUtc,
            contract.RecordedAtUtc,
            ToDomain(contract.UsagePolicy),
            contract.EvidenceDigest);
    }

    private static LocalizedTextValue ToDomainLocalizedText(LocalizedTextValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var locale = LocaleCode.Create(contract.Locale);
        return contract.State switch
        {
            FieldValueStateContract.Observed when
                !string.IsNullOrWhiteSpace(contract.Value) &&
                contract.AssertionId is { } assertionId && assertionId != Guid.Empty &&
                contract.MissingReason is null =>
                LocalizedTextValue.Observed(locale, contract.Value, assertionId),
            FieldValueStateContract.Missing when
                contract.Value is null &&
                contract.AssertionId is null &&
                contract.MissingReason is { } missingReason =>
                LocalizedTextValue.Missing(locale, ToDomain(missingReason)),
            FieldValueStateContract.Withheld when
                contract.Value is null &&
                contract.AssertionId is null &&
                contract.MissingReason is { } withheldReason =>
                LocalizedTextValue.Withheld(locale, ToDomain(withheldReason)),
            _ => throw InvalidShape(
                "catalog.localized_text_shape_invalid",
                $"Localized text for locale '{contract.Locale}' does not match state '{contract.State}'."),
        };
    }

    private static CategoryAssignment ToDomain(CategoryAssignmentContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return CategoryAssignment.Create(
            CategoryKey.Create(contract.CategoryKey),
            contract.AssertionId);
    }

    private static ListingAttributeValue ToDomain(ListingAttributeValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var key = AttributeKey.Create(contract.AttributeKey);
        return contract.State switch
        {
            FieldValueStateContract.Observed when
                contract.Value is not null &&
                contract.AssertionId is { } assertionId && assertionId != Guid.Empty &&
                contract.MissingReason is null =>
                ListingAttributeValue.Observed(key, ToDomain(contract.Value), assertionId),
            FieldValueStateContract.Missing when
                contract.Value is null &&
                contract.AssertionId is null &&
                contract.MissingReason is { } missingReason =>
                ListingAttributeValue.Missing(key, ToDomain(missingReason)),
            FieldValueStateContract.NotApplicable when
                contract.Value is null &&
                contract.AssertionId is null &&
                contract.MissingReason is null =>
                ListingAttributeValue.NotApplicable(key),
            FieldValueStateContract.Withheld =>
                throw InvalidShape(
                    "catalog.attribute_withheld_not_supported",
                    $"Attribute '{contract.AttributeKey}' cannot use withheld state until the Catalog storage contract supports it."),
            _ => throw InvalidShape(
                "catalog.attribute_value_shape_invalid",
                $"Attribute '{contract.AttributeKey}' does not match state '{contract.State}'."),
        };
    }

    private static TypedValue ToDomain(TypedValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return contract.Kind switch
        {
            AttributeValueKindContract.Boolean when
                contract.BooleanValue is { } booleanValue &&
                contract.DecimalValue is null &&
                contract.TextValue is null &&
                contract.TextSetValue is null =>
                TypedValue.Boolean(booleanValue),
            AttributeValueKindContract.Decimal when
                contract.BooleanValue is null &&
                contract.DecimalValue is { } decimalValue &&
                contract.TextValue is null &&
                contract.TextSetValue is null =>
                TypedValue.Decimal(decimalValue),
            AttributeValueKindContract.DurationMinutes when
                contract.BooleanValue is null &&
                contract.DecimalValue is { } duration &&
                contract.TextValue is null &&
                contract.TextSetValue is null =>
                TypedValue.DurationMinutes(ToDurationMinutes(duration)),
            AttributeValueKindContract.Text when
                contract.BooleanValue is null &&
                contract.DecimalValue is null &&
                !string.IsNullOrWhiteSpace(contract.TextValue) &&
                contract.TextSetValue is null =>
                TypedValue.Text(contract.TextValue),
            AttributeValueKindContract.TextSet when
                contract.BooleanValue is null &&
                contract.DecimalValue is null &&
                contract.TextValue is null &&
                contract.TextSetValue is not null =>
                TypedValue.TextSet(contract.TextSetValue),
            _ => throw InvalidShape(
                "catalog.typed_value_shape_invalid",
                $"Typed value payload does not match value kind '{contract.Kind}'."),
        };
    }

    private static ContactValue ToDomain(ContactValueContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return ContactValue.Create(
            ToDomain(contract.Kind),
            RequireAbsoluteUri(contract.Target, nameof(contract.Target)),
            contract.Label,
            contract.AssertionId);
    }

    private static MediaReference ToDomain(MediaReferenceContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return MediaReference.Create(
            contract.MediaId,
            RequireAbsoluteUri(contract.ObjectUri, nameof(contract.ObjectUri)),
            contract.ContentType,
            contract.ContentDigest,
            ToDomain(contract.RightsBasis),
            contract.RightsReference,
            contract.AssertionId);
    }

    private static int ToDurationMinutes(decimal value)
    {
        if (value < 0 || value > int.MaxValue || decimal.Truncate(value) != value)
        {
            throw InvalidShape(
                "catalog.duration_minutes_invalid",
                "Duration minutes must be a non-negative whole number within Int32 range.");
        }

        return decimal.ToInt32(value);
    }

    private static Uri RequireAbsoluteUri(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw InvalidShape(
                "catalog.uri_invalid",
                $"'{parameterName}' must be an absolute URI.");
        }

        return uri;
    }

    private static SubjectKind ToDomain(SubjectKindContract value) => value switch
    {
        SubjectKindContract.Organization => SubjectKind.Organization,
        SubjectKindContract.Place => SubjectKind.Place,
        SubjectKindContract.Provider => SubjectKind.Provider,
        _ => throw UnsupportedContractEnum(nameof(SubjectKindContract), value),
    };

    private static AttributeValueKind ToDomain(AttributeValueKindContract value) => value switch
    {
        AttributeValueKindContract.Boolean => AttributeValueKind.Boolean,
        AttributeValueKindContract.Decimal => AttributeValueKind.Decimal,
        AttributeValueKindContract.Text => AttributeValueKind.Text,
        AttributeValueKindContract.TextSet => AttributeValueKind.TextSet,
        AttributeValueKindContract.DurationMinutes => AttributeValueKind.DurationMinutes,
        _ => throw UnsupportedContractEnum(nameof(AttributeValueKindContract), value),
    };

    private static AttributeCardinality ToDomain(AttributeCardinalityContract value) => value switch
    {
        AttributeCardinalityContract.Single => AttributeCardinality.Single,
        AttributeCardinalityContract.Multiple => AttributeCardinality.Multiple,
        _ => throw UnsupportedContractEnum(nameof(AttributeCardinalityContract), value),
    };

    private static PublicFieldRequirement ToDomain(PublicFieldRequirementContract value) => value switch
    {
        PublicFieldRequirementContract.Optional => PublicFieldRequirement.Optional,
        PublicFieldRequirementContract.RequiredForPublication => PublicFieldRequirement.RequiredForPublication,
        _ => throw UnsupportedContractEnum(nameof(PublicFieldRequirementContract), value),
    };

    private static SourceKind ToDomain(SourceKindContract value) => value switch
    {
        SourceKindContract.FirstPartySubmission => SourceKind.FirstPartySubmission,
        SourceKindContract.PublicWebsite => SourceKind.PublicWebsite,
        SourceKindContract.PublicDirectoryReference => SourceKind.PublicDirectoryReference,
        SourceKindContract.EditorialResearch => SourceKind.EditorialResearch,
        SourceKindContract.OwnerVerification => SourceKind.OwnerVerification,
        SourceKindContract.LicensedDataset => SourceKind.LicensedDataset,
        _ => throw UnsupportedContractEnum(nameof(SourceKindContract), value),
    };

    private static UsagePolicy ToDomain(UsagePolicyContract value) => value switch
    {
        UsagePolicyContract.PublicAllowed => UsagePolicy.PublicAllowed,
        UsagePolicyContract.ReferenceOnly => UsagePolicy.ReferenceOnly,
        UsagePolicyContract.ResearchOnly => UsagePolicy.ResearchOnly,
        UsagePolicyContract.Forbidden => UsagePolicy.Forbidden,
        _ => throw UnsupportedContractEnum(nameof(UsagePolicyContract), value),
    };

    private static MissingValueReason ToDomain(MissingValueReasonContract value) => value switch
    {
        MissingValueReasonContract.NotPublishedBySource => MissingValueReason.NotPublishedBySource,
        MissingValueReasonContract.NotCollected => MissingValueReason.NotCollected,
        MissingValueReasonContract.ConflictingEvidence => MissingValueReason.ConflictingEvidence,
        MissingValueReasonContract.RightsRestricted => MissingValueReason.RightsRestricted,
        MissingValueReasonContract.OwnerWithheld => MissingValueReason.OwnerWithheld,
        _ => throw UnsupportedContractEnum(nameof(MissingValueReasonContract), value),
    };

    private static GeographyState ToDomain(GeographyStateContract value) => value switch
    {
        GeographyStateContract.BerlinCore => GeographyState.BerlinCore,
        GeographyStateContract.BerlinNearby => GeographyState.BerlinNearby,
        GeographyStateContract.RemoteOnly => GeographyState.RemoteOnly,
        GeographyStateContract.OutsideMarket => GeographyState.OutsideMarket,
        GeographyStateContract.Unresolved => GeographyState.Unresolved,
        _ => throw UnsupportedContractEnum(nameof(GeographyStateContract), value),
    };

    private static ContactKind ToDomain(ContactKindContract value) => value switch
    {
        ContactKindContract.Website => ContactKind.Website,
        ContactKindContract.Email => ContactKind.Email,
        ContactKindContract.Phone => ContactKind.Phone,
        ContactKindContract.WhatsApp => ContactKind.WhatsApp,
        ContactKindContract.BookingReference => ContactKind.BookingReference,
        ContactKindContract.MapReference => ContactKind.MapReference,
        _ => throw UnsupportedContractEnum(nameof(ContactKindContract), value),
    };

    private static MediaRightsBasis ToDomain(MediaRightsBasisContract value) => value switch
    {
        MediaRightsBasisContract.OwnerProvided => MediaRightsBasis.OwnerProvided,
        MediaRightsBasisContract.ExplicitLicense => MediaRightsBasis.ExplicitLicense,
        MediaRightsBasisContract.OriginalEditorialWork => MediaRightsBasis.OriginalEditorialWork,
        MediaRightsBasisContract.PublicDomain => MediaRightsBasis.PublicDomain,
        _ => throw UnsupportedContractEnum(nameof(MediaRightsBasisContract), value),
    };

    private static SubjectReferenceContract ToContract(SubjectReference subject) =>
        new(subject.SubjectId, subject.SubjectRevisionId, ToContract(subject.Kind));

    private static SubjectKindContract ToContract(SubjectKind value) => value switch
    {
        SubjectKind.Organization => SubjectKindContract.Organization,
        SubjectKind.Place => SubjectKindContract.Place,
        SubjectKind.Provider => SubjectKindContract.Provider,
        _ => throw UnsupportedDomainEnum(nameof(SubjectKind), value),
    };

    private static ListingLifecycleStateContract ToContract(ListingLifecycleState value) => value switch
    {
        ListingLifecycleState.Draft => ListingLifecycleStateContract.Draft,
        ListingLifecycleState.Approved => ListingLifecycleStateContract.Approved,
        ListingLifecycleState.Published => ListingLifecycleStateContract.Published,
        ListingLifecycleState.Archived => ListingLifecycleStateContract.Archived,
        _ => throw UnsupportedDomainEnum(nameof(ListingLifecycleState), value),
    };

    private static ClaimStateContract ToContract(ClaimState value) => value switch
    {
        ClaimState.Pending => ClaimStateContract.Pending,
        ClaimState.Verified => ClaimStateContract.Verified,
        ClaimState.Rejected => ClaimStateContract.Rejected,
        ClaimState.Revoked => ClaimStateContract.Revoked,
        _ => throw UnsupportedDomainEnum(nameof(ClaimState), value),
    };

    private static ListingAccessScopeContract ToContract(ListingAccessScope value) => value switch
    {
        ListingAccessScope.ReadDraft => ListingAccessScopeContract.ReadDraft,
        ListingAccessScope.ProposeRevision => ListingAccessScopeContract.ProposeRevision,
        ListingAccessScope.ManageContacts => ListingAccessScopeContract.ManageContacts,
        ListingAccessScope.ManageMedia => ListingAccessScopeContract.ManageMedia,
        _ => throw UnsupportedDomainEnum(nameof(ListingAccessScope), value),
    };

    private static CatalogContractException UnsupportedContractEnum<T>(string enumName, T value)
        where T : struct, Enum =>
        new(
            "catalog.contract_enum_unsupported",
            $"Value '{value}' is not supported for contract enum '{enumName}'.");

    private static CatalogContractException UnsupportedDomainEnum<T>(string enumName, T value)
        where T : struct, Enum =>
        new(
            "catalog.domain_enum_unsupported",
            $"Value '{value}' is not supported for domain enum '{enumName}'.");

    private static CatalogContractException InvalidShape(string code, string message) =>
        new(code, message);
}
