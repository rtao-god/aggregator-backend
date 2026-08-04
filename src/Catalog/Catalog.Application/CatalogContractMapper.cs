using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

internal static class CatalogContractMapper
{
    public static ListingDto ToDto(Listing listing)
    {
        ArgumentNullException.ThrowIfNull(listing);
        return new ListingDto(
            listing.Id.Value,
            listing.CatalogId.Value,
            ToContract(listing.Subject.Kind),
            listing.Subject.SubjectId,
            ToContract(listing.State),
            listing.CurrentDraftRevisionId?.Value,
            listing.CurrentPublishedRevisionId?.Value,
            listing.CurrentPublicationId?.Value,
            listing.AggregateRevision,
            listing.ArchiveReason,
            listing.LastChangedBy.Value,
            listing.LastChangedAtUtc);
    }

    public static ListingRevisionDto ToDto(ListingRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        return new ListingRevisionDto(
            revision.Id.Value,
            revision.ListingId.Value,
            revision.SubjectRevisionId.Value,
            revision.ProductConfigurationRevisionId.Value,
            revision.TaxonomyRevisionId.Value,
            revision.AttributeRevisionId.Value,
            revision.MarketAreaRevisionId.Value,
            revision.Translations
                .Select(item => new LocalizedListingContentDto(item.Locale, item.Title, item.Summary))
                .ToArray(),
            revision.CategoryKeys.ToArray(),
            revision.Attributes
                .Select(item => new ListingAttributeValueDto(
                    item.AttributeKey,
                    ToContract(item.DataType),
                    ToContract(item.State),
                    item.Value))
                .ToArray(),
            revision.Provenance
                .Select(item => new ProvenanceReferenceDto(
                    item.FieldPath,
                    item.SourceKind,
                    item.SourceReference,
                    ToContract(item.UsagePolicy),
                    item.ObservedAtUtc,
                    item.ValidUntilUtc))
                .ToArray(),
            revision.ContentDigest,
            revision.CreatedBy.Value,
            revision.CreatedAtUtc);
    }

    public static PublicationRequestDto ToDto(CatalogPublicationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new PublicationRequestDto(
            request.Id.Value,
            request.PublicationId.Value,
            request.CatalogId.Value,
            request.ExpectedCurrentPublicationId?.Value,
            request.ProductConfigurationRevisionId.Value,
            request.TaxonomyRevisionId.Value,
            request.AttributeRevisionId.Value,
            request.MarketAreaRevisionId.Value,
            request.SelectedListings
                .Select(item => new SelectedListingRevisionDto(item.ListingId.Value, item.ListingRevisionId.Value))
                .ToArray(),
            request.Reason,
            request.RequestedBy.Value,
            request.RequestedAtUtc,
            ToContract(request.State),
            request.AggregateRevision,
            request.FailureCode);
    }

    public static ListingKind ToDomain(ListingKindContract value) => value switch
    {
        ListingKindContract.Place => ListingKind.Place,
        ListingKindContract.Provider => ListingKind.Provider,
        _ => throw UnsupportedEnum(nameof(ListingKindContract), value),
    };

    public static AttributeDataType ToDomain(AttributeDataTypeContract value) => value switch
    {
        AttributeDataTypeContract.Boolean => AttributeDataType.Boolean,
        AttributeDataTypeContract.Integer => AttributeDataType.Integer,
        AttributeDataTypeContract.Decimal => AttributeDataType.Decimal,
        AttributeDataTypeContract.Money => AttributeDataType.Money,
        AttributeDataTypeContract.ShortText => AttributeDataType.ShortText,
        AttributeDataTypeContract.LongText => AttributeDataType.LongText,
        AttributeDataTypeContract.LocalizedText => AttributeDataType.LocalizedText,
        AttributeDataTypeContract.Date => AttributeDataType.Date,
        AttributeDataTypeContract.DateTime => AttributeDataType.DateTime,
        AttributeDataTypeContract.Duration => AttributeDataType.Duration,
        AttributeDataTypeContract.SingleOption => AttributeDataType.SingleOption,
        AttributeDataTypeContract.MultiOption => AttributeDataType.MultiOption,
        AttributeDataTypeContract.Measurement => AttributeDataType.Measurement,
        AttributeDataTypeContract.PhoneCapability => AttributeDataType.PhoneCapability,
        AttributeDataTypeContract.ExternalReferenceCapability => AttributeDataType.ExternalReferenceCapability,
        AttributeDataTypeContract.GeoClassification => AttributeDataType.GeoClassification,
        _ => throw UnsupportedEnum(nameof(AttributeDataTypeContract), value),
    };

    public static ConfiguredAttributeDataType ToConfiguredDomain(AttributeDataTypeContract value) => value switch
    {
        AttributeDataTypeContract.Boolean => ConfiguredAttributeDataType.Boolean,
        AttributeDataTypeContract.Integer => ConfiguredAttributeDataType.Integer,
        AttributeDataTypeContract.Decimal => ConfiguredAttributeDataType.Decimal,
        AttributeDataTypeContract.Money => ConfiguredAttributeDataType.Money,
        AttributeDataTypeContract.ShortText => ConfiguredAttributeDataType.ShortText,
        AttributeDataTypeContract.LongText => ConfiguredAttributeDataType.LongText,
        AttributeDataTypeContract.LocalizedText => ConfiguredAttributeDataType.LocalizedText,
        AttributeDataTypeContract.Date => ConfiguredAttributeDataType.Date,
        AttributeDataTypeContract.DateTime => ConfiguredAttributeDataType.DateTime,
        AttributeDataTypeContract.Duration => ConfiguredAttributeDataType.Duration,
        AttributeDataTypeContract.SingleOption => ConfiguredAttributeDataType.SingleOption,
        AttributeDataTypeContract.MultiOption => ConfiguredAttributeDataType.MultiOption,
        AttributeDataTypeContract.Measurement => ConfiguredAttributeDataType.Measurement,
        AttributeDataTypeContract.PhoneCapability => ConfiguredAttributeDataType.PhoneCapability,
        AttributeDataTypeContract.ExternalReferenceCapability => ConfiguredAttributeDataType.ExternalReferenceCapability,
        AttributeDataTypeContract.GeoClassification => ConfiguredAttributeDataType.GeoClassification,
        _ => throw UnsupportedEnum(nameof(AttributeDataTypeContract), value),
    };

    public static AttributeValueState ToDomain(AttributeValueStateContract value) => value switch
    {
        AttributeValueStateContract.Observed => AttributeValueState.Observed,
        AttributeValueStateContract.OwnerConfirmed => AttributeValueState.OwnerConfirmed,
        AttributeValueStateContract.EditorConfirmed => AttributeValueState.EditorConfirmed,
        AttributeValueStateContract.Unknown => AttributeValueState.Unknown,
        AttributeValueStateContract.NotDisclosed => AttributeValueState.NotDisclosed,
        AttributeValueStateContract.NotApplicable => AttributeValueState.NotApplicable,
        AttributeValueStateContract.Disputed => AttributeValueState.Disputed,
        AttributeValueStateContract.Expired => AttributeValueState.Expired,
        _ => throw UnsupportedEnum(nameof(AttributeValueStateContract), value),
    };

    public static ProvenanceUsagePolicy ToDomain(ProvenanceUsagePolicyContract value) => value switch
    {
        ProvenanceUsagePolicyContract.CommercialAllowed => ProvenanceUsagePolicy.CommercialAllowed,
        ProvenanceUsagePolicyContract.ReferenceOnly => ProvenanceUsagePolicy.ReferenceOnly,
        ProvenanceUsagePolicyContract.ResearchOnly => ProvenanceUsagePolicy.ResearchOnly,
        ProvenanceUsagePolicyContract.Forbidden => ProvenanceUsagePolicy.Forbidden,
        ProvenanceUsagePolicyContract.Unknown => ProvenanceUsagePolicy.Unknown,
        _ => throw UnsupportedEnum(nameof(ProvenanceUsagePolicyContract), value),
    };

    public static ConfiguredListingKind ToConfiguredDomain(ListingKindContract value) => value switch
    {
        ListingKindContract.Place => ConfiguredListingKind.Place,
        ListingKindContract.Provider => ConfiguredListingKind.Provider,
        _ => throw UnsupportedEnum(nameof(ListingKindContract), value),
    };

    private static ListingKindContract ToContract(ListingKind value) => value switch
    {
        ListingKind.Place => ListingKindContract.Place,
        ListingKind.Provider => ListingKindContract.Provider,
        _ => throw UnsupportedEnum(nameof(ListingKind), value),
    };

    private static ListingLifecycleStateContract ToContract(ListingLifecycleState value) => value switch
    {
        ListingLifecycleState.Created => ListingLifecycleStateContract.Created,
        ListingLifecycleState.Draft => ListingLifecycleStateContract.Draft,
        ListingLifecycleState.ReviewRequired => ListingLifecycleStateContract.ReviewRequired,
        ListingLifecycleState.Approved => ListingLifecycleStateContract.Approved,
        ListingLifecycleState.PublicationRequested => ListingLifecycleStateContract.PublicationRequested,
        ListingLifecycleState.Published => ListingLifecycleStateContract.Published,
        ListingLifecycleState.Stale => ListingLifecycleStateContract.Stale,
        ListingLifecycleState.Archived => ListingLifecycleStateContract.Archived,
        ListingLifecycleState.Rejected => ListingLifecycleStateContract.Rejected,
        ListingLifecycleState.Disputed => ListingLifecycleStateContract.Disputed,
        ListingLifecycleState.Blocked => ListingLifecycleStateContract.Blocked,
        _ => throw UnsupportedEnum(nameof(ListingLifecycleState), value),
    };

    private static PublicationRequestStateContract ToContract(PublicationRequestState value) => value switch
    {
        PublicationRequestState.Pending => PublicationRequestStateContract.Pending,
        PublicationRequestState.Processing => PublicationRequestStateContract.Processing,
        PublicationRequestState.Sealed => PublicationRequestStateContract.Sealed,
        PublicationRequestState.Failed => PublicationRequestStateContract.Failed,
        PublicationRequestState.Cancelled => PublicationRequestStateContract.Cancelled,
        _ => throw UnsupportedEnum(nameof(PublicationRequestState), value),
    };

    private static AttributeDataTypeContract ToContract(AttributeDataType value) => value switch
    {
        AttributeDataType.Boolean => AttributeDataTypeContract.Boolean,
        AttributeDataType.Integer => AttributeDataTypeContract.Integer,
        AttributeDataType.Decimal => AttributeDataTypeContract.Decimal,
        AttributeDataType.Money => AttributeDataTypeContract.Money,
        AttributeDataType.ShortText => AttributeDataTypeContract.ShortText,
        AttributeDataType.LongText => AttributeDataTypeContract.LongText,
        AttributeDataType.LocalizedText => AttributeDataTypeContract.LocalizedText,
        AttributeDataType.Date => AttributeDataTypeContract.Date,
        AttributeDataType.DateTime => AttributeDataTypeContract.DateTime,
        AttributeDataType.Duration => AttributeDataTypeContract.Duration,
        AttributeDataType.SingleOption => AttributeDataTypeContract.SingleOption,
        AttributeDataType.MultiOption => AttributeDataTypeContract.MultiOption,
        AttributeDataType.Measurement => AttributeDataTypeContract.Measurement,
        AttributeDataType.PhoneCapability => AttributeDataTypeContract.PhoneCapability,
        AttributeDataType.ExternalReferenceCapability => AttributeDataTypeContract.ExternalReferenceCapability,
        AttributeDataType.GeoClassification => AttributeDataTypeContract.GeoClassification,
        _ => throw UnsupportedEnum(nameof(AttributeDataType), value),
    };

    private static AttributeValueStateContract ToContract(AttributeValueState value) => value switch
    {
        AttributeValueState.Observed => AttributeValueStateContract.Observed,
        AttributeValueState.OwnerConfirmed => AttributeValueStateContract.OwnerConfirmed,
        AttributeValueState.EditorConfirmed => AttributeValueStateContract.EditorConfirmed,
        AttributeValueState.Unknown => AttributeValueStateContract.Unknown,
        AttributeValueState.NotDisclosed => AttributeValueStateContract.NotDisclosed,
        AttributeValueState.NotApplicable => AttributeValueStateContract.NotApplicable,
        AttributeValueState.Disputed => AttributeValueStateContract.Disputed,
        AttributeValueState.Expired => AttributeValueStateContract.Expired,
        _ => throw UnsupportedEnum(nameof(AttributeValueState), value),
    };

    private static ProvenanceUsagePolicyContract ToContract(ProvenanceUsagePolicy value) => value switch
    {
        ProvenanceUsagePolicy.CommercialAllowed => ProvenanceUsagePolicyContract.CommercialAllowed,
        ProvenanceUsagePolicy.ReferenceOnly => ProvenanceUsagePolicyContract.ReferenceOnly,
        ProvenanceUsagePolicy.ResearchOnly => ProvenanceUsagePolicyContract.ResearchOnly,
        ProvenanceUsagePolicy.Forbidden => ProvenanceUsagePolicyContract.Forbidden,
        ProvenanceUsagePolicy.Unknown => ProvenanceUsagePolicyContract.Unknown,
        _ => throw UnsupportedEnum(nameof(ProvenanceUsagePolicy), value),
    };

    private static CatalogCommandException UnsupportedEnum<T>(string enumName, T value)
        where T : struct, Enum =>
        new(
            "Catalog.Contracts",
            "CONTRACT_ENUM_UNSUPPORTED",
            400,
            $"Value '{value}' is not supported for enum '{enumName}'.",
            "Use one of the enum values declared by the current Catalog contract.");
}
