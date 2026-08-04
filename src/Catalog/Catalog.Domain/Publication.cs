using System.Collections.Immutable;

namespace Aggregator.Catalog.Domain;

public enum PublicationRequestState
{
    Pending = 1,
    Processing = 2,
    Sealed = 3,
    Failed = 4,
    Cancelled = 5,
}

public sealed record SelectedListingRevision(ListingId ListingId, ListingRevisionId ListingRevisionId);

public sealed class CatalogPublicationRequest
{
    private CatalogPublicationRequest(
        PublicationRequestId id,
        PublicationId publicationId,
        CatalogId catalogId,
        PublicationId? expectedCurrentPublicationId,
        ProductConfigurationRevisionId productConfigurationRevisionId,
        TaxonomyRevisionId taxonomyRevisionId,
        AttributeRevisionId attributeRevisionId,
        MarketAreaRevisionId marketAreaRevisionId,
        ImmutableArray<SelectedListingRevision> selectedListings,
        string reason,
        ActorId requestedBy,
        DateTimeOffset requestedAtUtc,
        PublicationRequestState state,
        long aggregateRevision,
        string? failureCode)
    {
        Id = id;
        PublicationId = publicationId;
        CatalogId = catalogId;
        ExpectedCurrentPublicationId = expectedCurrentPublicationId;
        ProductConfigurationRevisionId = productConfigurationRevisionId;
        TaxonomyRevisionId = taxonomyRevisionId;
        AttributeRevisionId = attributeRevisionId;
        MarketAreaRevisionId = marketAreaRevisionId;
        SelectedListings = selectedListings;
        Reason = reason;
        RequestedBy = requestedBy;
        RequestedAtUtc = requestedAtUtc;
        State = state;
        AggregateRevision = aggregateRevision;
        FailureCode = failureCode;
    }

    public PublicationRequestId Id { get; }

    public PublicationId PublicationId { get; }

    public CatalogId CatalogId { get; }

    public PublicationId? ExpectedCurrentPublicationId { get; }

    public ProductConfigurationRevisionId ProductConfigurationRevisionId { get; }

    public TaxonomyRevisionId TaxonomyRevisionId { get; }

    public AttributeRevisionId AttributeRevisionId { get; }

    public MarketAreaRevisionId MarketAreaRevisionId { get; }

    public ImmutableArray<SelectedListingRevision> SelectedListings { get; }

    public string Reason { get; }

    public ActorId RequestedBy { get; }

    public DateTimeOffset RequestedAtUtc { get; }

    public PublicationRequestState State { get; private set; }

    public long AggregateRevision { get; private set; }

    public string? FailureCode { get; private set; }

    public static CatalogPublicationRequest Create(
        PublicationRequestId id,
        PublicationId publicationId,
        CatalogId catalogId,
        PublicationId? expectedCurrentPublicationId,
        ProductConfigurationRevisionId productConfigurationRevisionId,
        TaxonomyRevisionId taxonomyRevisionId,
        AttributeRevisionId attributeRevisionId,
        MarketAreaRevisionId marketAreaRevisionId,
        IEnumerable<SelectedListingRevision> selectedListings,
        string reason,
        ActorId requestedBy,
        DateTimeOffset requestedAtUtc)
    {
        ValidateIdentifiers(
            id,
            publicationId,
            catalogId,
            productConfigurationRevisionId,
            taxonomyRevisionId,
            attributeRevisionId,
            marketAreaRevisionId,
            requestedBy);
        if (expectedCurrentPublicationId is { } expected)
        {
            CatalogTextRules.RequireIdentifier(expected.Value, nameof(expectedCurrentPublicationId));
        }

        ArgumentNullException.ThrowIfNull(selectedListings);
        CatalogTextRules.RequireText(reason, nameof(reason), 1_000);
        CatalogTextRules.RequireUtc(requestedAtUtc, nameof(requestedAtUtc));
        var selected = selectedListings.OrderBy(item => item.ListingId.Value).ToImmutableArray();
        if (selected.IsDefaultOrEmpty)
        {
            throw new CatalogDomainException("PUBLICATION_LISTING_REQUIRED", "A publication request must select at least one listing revision.");
        }

        var listingIds = new HashSet<ListingId>();
        foreach (var item in selected)
        {
            CatalogTextRules.RequireIdentifier(item.ListingId.Value, nameof(item.ListingId));
            CatalogTextRules.RequireIdentifier(item.ListingRevisionId.Value, nameof(item.ListingRevisionId));
            if (!listingIds.Add(item.ListingId))
            {
                throw new CatalogDomainException(
                    "PUBLICATION_LISTING_DUPLICATE",
                    $"Listing '{item.ListingId}' is selected more than once.");
            }
        }

        return new CatalogPublicationRequest(
            id,
            publicationId,
            catalogId,
            expectedCurrentPublicationId,
            productConfigurationRevisionId,
            taxonomyRevisionId,
            attributeRevisionId,
            marketAreaRevisionId,
            selected,
            reason,
            requestedBy,
            requestedAtUtc,
            PublicationRequestState.Pending,
            1,
            null);
    }

    public static CatalogPublicationRequest Restore(
        PublicationRequestId id,
        PublicationId publicationId,
        CatalogId catalogId,
        PublicationId? expectedCurrentPublicationId,
        ProductConfigurationRevisionId productConfigurationRevisionId,
        TaxonomyRevisionId taxonomyRevisionId,
        AttributeRevisionId attributeRevisionId,
        MarketAreaRevisionId marketAreaRevisionId,
        IEnumerable<SelectedListingRevision> selectedListings,
        string reason,
        ActorId requestedBy,
        DateTimeOffset requestedAtUtc,
        PublicationRequestState state,
        long aggregateRevision,
        string? failureCode)
    {
        var request = Create(
            id,
            publicationId,
            catalogId,
            expectedCurrentPublicationId,
            productConfigurationRevisionId,
            taxonomyRevisionId,
            attributeRevisionId,
            marketAreaRevisionId,
            selectedListings,
            reason,
            requestedBy,
            requestedAtUtc);
        if (aggregateRevision < 1)
        {
            throw new CatalogDomainException("PUBLICATION_REQUEST_REVISION_INVALID", "Publication request revision must be positive.");
        }

        request.State = state;
        request.AggregateRevision = aggregateRevision;
        request.FailureCode = failureCode;
        return request;
    }

    public void Start(long expectedAggregateRevision)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State != PublicationRequestState.Pending)
        {
            throw new CatalogDomainException("PUBLICATION_REQUEST_STATE_INVALID", "Only a pending publication request can start.");
        }

        State = PublicationRequestState.Processing;
        AggregateRevision++;
    }

    public void MarkSealed(long expectedAggregateRevision)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State != PublicationRequestState.Processing)
        {
            throw new CatalogDomainException("PUBLICATION_REQUEST_STATE_INVALID", "Only a processing publication request can be sealed.");
        }

        State = PublicationRequestState.Sealed;
        FailureCode = null;
        AggregateRevision++;
    }

    public void MarkFailed(string failureCode, long expectedAggregateRevision)
    {
        EnsureRevision(expectedAggregateRevision);
        if (State != PublicationRequestState.Processing)
        {
            throw new CatalogDomainException("PUBLICATION_REQUEST_STATE_INVALID", "Only a processing publication request can fail.");
        }

        CatalogTextRules.RequireKey(failureCode, nameof(failureCode));
        State = PublicationRequestState.Failed;
        FailureCode = failureCode;
        AggregateRevision++;
    }

    private void EnsureRevision(long expectedAggregateRevision)
    {
        if (expectedAggregateRevision != AggregateRevision)
        {
            throw new CatalogDomainException(
                "PUBLICATION_REQUEST_REVISION_CONFLICT",
                $"Expected publication request revision {expectedAggregateRevision}, actual revision {AggregateRevision}.");
        }
    }

    private static void ValidateIdentifiers(
        PublicationRequestId id,
        PublicationId publicationId,
        CatalogId catalogId,
        ProductConfigurationRevisionId productConfigurationRevisionId,
        TaxonomyRevisionId taxonomyRevisionId,
        AttributeRevisionId attributeRevisionId,
        MarketAreaRevisionId marketAreaRevisionId,
        ActorId requestedBy)
    {
        CatalogTextRules.RequireIdentifier(id.Value, nameof(id));
        CatalogTextRules.RequireIdentifier(publicationId.Value, nameof(publicationId));
        CatalogTextRules.RequireIdentifier(catalogId.Value, nameof(catalogId));
        CatalogTextRules.RequireIdentifier(productConfigurationRevisionId.Value, nameof(productConfigurationRevisionId));
        CatalogTextRules.RequireIdentifier(taxonomyRevisionId.Value, nameof(taxonomyRevisionId));
        CatalogTextRules.RequireIdentifier(attributeRevisionId.Value, nameof(attributeRevisionId));
        CatalogTextRules.RequireIdentifier(marketAreaRevisionId.Value, nameof(marketAreaRevisionId));
        CatalogTextRules.RequireIdentifier(requestedBy.Value, nameof(requestedBy));
    }
}

public sealed record CatalogPublication(
    PublicationId Id,
    CatalogId CatalogId,
    PublicationId? PreviousPublicationId,
    ProductConfigurationRevisionId ProductConfigurationRevisionId,
    TaxonomyRevisionId TaxonomyRevisionId,
    AttributeRevisionId AttributeRevisionId,
    MarketAreaRevisionId MarketAreaRevisionId,
    string ArtifactKey,
    string ArtifactDigest,
    long ArtifactSize,
    int ListingCount,
    DateTimeOffset SealedAtUtc,
    ActorId ActivatedBy);
