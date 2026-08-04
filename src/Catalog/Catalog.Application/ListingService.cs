using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class ListingService
{
    private readonly IListingStore _listingStore;
    private readonly IProductConfigurationStore _configurationStore;
    private readonly TimeProvider _timeProvider;

    public ListingService(
        IListingStore listingStore,
        IProductConfigurationStore configurationStore,
        TimeProvider timeProvider)
    {
        _listingStore = listingStore ?? throw new ArgumentNullException(nameof(listingStore));
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<ListingDto> CreateAsync(
        CreateListingRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureIdentifier(request.CatalogId, nameof(request.CatalogId));
        EnsureIdentifier(request.SubjectId, nameof(request.SubjectId));
        EnsureIdentifier(actorId.Value, nameof(actorId));
        var subject = request.ListingKind switch
        {
            ListingKindContract.Place => ListingSubject.ForPlace(new PlaceId(request.SubjectId)),
            ListingKindContract.Provider => ListingSubject.ForProvider(new ProviderId(request.SubjectId)),
            _ => throw UnsupportedListingKind(request.ListingKind),
        };
        var listing = Listing.Create(
            ListingId.New(),
            new CatalogId(request.CatalogId),
            subject,
            actorId,
            _timeProvider.GetUtcNow());
        var command = CatalogCommandIdentity.Create(
            $"listing/create/{request.CatalogId:D}",
            idempotencyKey,
            CatalogCanonicalJson.ComputeDigest(request));
        var result = await _listingStore.CreateAsync(listing, command, cancellationToken);
        return CatalogContractMapper.ToDto(result.Value);
    }

    public async Task<ListingDto> GetAsync(ListingId listingId, CancellationToken cancellationToken)
    {
        EnsureIdentifier(listingId.Value, nameof(listingId));
        var listing = await GetRequiredListingAsync(listingId, cancellationToken);
        return CatalogContractMapper.ToDto(listing);
    }

    public async Task<ListingRevisionCreatedDto> CreateRevisionAsync(
        ListingId listingId,
        long expectedAggregateRevision,
        CreateListingRevisionRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        EnsureIdentifier(listingId.Value, nameof(listingId));
        EnsureExpectedRevision(expectedAggregateRevision);
        ArgumentNullException.ThrowIfNull(request);
        EnsureIdentifier(actorId.Value, nameof(actorId));
        ValidateRevisionRequestCollections(request);
        var listing = await GetRequiredListingAsync(listingId, cancellationToken);
        var configuration = await _configurationStore.GetRevisionAsync(
            new ProductConfigurationRevisionId(request.ProductConfigurationRevisionId),
            cancellationToken)
            ?? throw NotFound(
                "PRODUCT_CONFIGURATION_REVISION_NOT_FOUND",
                "The exact product configuration revision does not exist.",
                new Dictionary<string, object?> { ["revisionId"] = request.ProductConfigurationRevisionId });
        var normalized = Normalize(request);
        var canonical = new ListingRevisionCanonical(listingId.Value, normalized);
        var revision = ListingRevision.Create(
            ListingRevisionId.New(),
            listingId,
            new SubjectRevisionId(normalized.SubjectRevisionId),
            new ProductConfigurationRevisionId(normalized.ProductConfigurationRevisionId),
            new TaxonomyRevisionId(normalized.TaxonomyRevisionId),
            new AttributeRevisionId(normalized.AttributeRevisionId),
            new MarketAreaRevisionId(normalized.MarketAreaRevisionId),
            normalized.Translations.Select(item => new LocalizedListingContent(item.Locale, item.Title, item.Summary)),
            normalized.CategoryKeys,
            normalized.Attributes.Select(item => ListingAttributeValue.Create(
                item.AttributeKey,
                CatalogContractMapper.ToDomain(item.DataType),
                CatalogContractMapper.ToDomain(item.State),
                item.Value)),
            normalized.Provenance.Select(item => new ProvenanceReference(
                item.FieldPath,
                item.SourceKind,
                item.SourceReference,
                CatalogContractMapper.ToDomain(item.UsagePolicy),
                item.ObservedAtUtc,
                item.ValidUntilUtc)),
            CatalogCanonicalJson.ComputeDigest(canonical),
            actorId,
            _timeProvider.GetUtcNow());
        ListingConfigurationValidator.ValidateForDraft(listing, revision, configuration);
        var expectedStoredRevision = listing.AggregateRevision;
        listing.AttachDraftRevision(revision.Id, expectedAggregateRevision, actorId, _timeProvider.GetUtcNow());
        var command = CatalogCommandIdentity.Create(
            $"listing/revision/{listingId.Value:D}",
            idempotencyKey,
            revision.ContentDigest);
        var result = await _listingStore.SaveRevisionAsync(
            listing,
            revision,
            expectedStoredRevision,
            "draft-revision-created",
            command,
            cancellationToken);
        return new ListingRevisionCreatedDto(
            CatalogContractMapper.ToDto(result.Value.Listing),
            CatalogContractMapper.ToDto(result.Value.Revision));
    }

    public Task<ListingDto> SubmitForReviewAsync(
        ListingId listingId,
        long expectedAggregateRevision,
        ListingLifecycleCommandRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        MutateLifecycleAsync(
            listingId,
            expectedAggregateRevision,
            request,
            actorId,
            idempotencyKey,
            "submit-review",
            static (listing, expected, actor, timestamp) => listing.SubmitForReview(expected, actor, timestamp),
            validateForPublication: false,
            cancellationToken);

    public Task<ListingDto> ApproveAsync(
        ListingId listingId,
        long expectedAggregateRevision,
        ListingLifecycleCommandRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        MutateLifecycleAsync(
            listingId,
            expectedAggregateRevision,
            request,
            actorId,
            idempotencyKey,
            "approve",
            static (listing, expected, actor, timestamp) => listing.Approve(expected, actor, timestamp),
            validateForPublication: true,
            cancellationToken);

    public Task<ListingDto> RejectAsync(
        ListingId listingId,
        long expectedAggregateRevision,
        ListingLifecycleCommandRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        MutateLifecycleAsync(
            listingId,
            expectedAggregateRevision,
            request,
            actorId,
            idempotencyKey,
            "reject",
            static (listing, expected, actor, timestamp) => listing.Reject(expected, actor, timestamp),
            validateForPublication: false,
            cancellationToken);

    public Task<ListingDto> DisputeAsync(
        ListingId listingId,
        long expectedAggregateRevision,
        ListingLifecycleCommandRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        MutateLifecycleAsync(
            listingId,
            expectedAggregateRevision,
            request,
            actorId,
            idempotencyKey,
            "dispute",
            static (listing, expected, actor, timestamp) => listing.Dispute(expected, actor, timestamp),
            validateForPublication: false,
            cancellationToken);

    public Task<ListingDto> ArchiveAsync(
        ListingId listingId,
        long expectedAggregateRevision,
        ListingLifecycleCommandRequest request,
        ActorId actorId,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        MutateLifecycleAsync(
            listingId,
            expectedAggregateRevision,
            request,
            actorId,
            idempotencyKey,
            "archive",
            static (listing, expected, actor, timestamp) => listing.Archive("editorial-archive", expected, actor, timestamp),
            validateForPublication: false,
            cancellationToken);

    private async Task<ListingDto> MutateLifecycleAsync(
        ListingId listingId,
        long expectedAggregateRevision,
        ListingLifecycleCommandRequest request,
        ActorId actorId,
        string idempotencyKey,
        string operation,
        Action<Listing, long, ActorId, DateTimeOffset> mutation,
        bool validateForPublication,
        CancellationToken cancellationToken)
    {
        EnsureIdentifier(listingId.Value, nameof(listingId));
        EnsureExpectedRevision(expectedAggregateRevision);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutation);
        EnsureIdentifier(actorId.Value, nameof(actorId));
        RequireReason(request.Reason);
        var listing = await GetRequiredListingAsync(listingId, cancellationToken);
        if (validateForPublication)
        {
            var revisionId = listing.CurrentDraftRevisionId
                ?? throw new CatalogCommandException(
                    "Catalog.Listings",
                    "LISTING_DRAFT_POINTER_REQUIRED",
                    409,
                    "The listing has no current draft revision.",
                    "Create a draft revision before editorial approval.");
            var revision = await _listingStore.GetRevisionAsync(revisionId, cancellationToken)
                ?? throw NotFound(
                    "LISTING_REVISION_NOT_FOUND",
                    "The current draft revision cannot be loaded.",
                    new Dictionary<string, object?> { ["listingRevisionId"] = revisionId.Value });
            var configuration = await _configurationStore.GetRevisionAsync(
                revision.ProductConfigurationRevisionId,
                cancellationToken)
                ?? throw NotFound(
                    "PRODUCT_CONFIGURATION_REVISION_NOT_FOUND",
                    "The exact product configuration revision cannot be loaded.",
                    new Dictionary<string, object?> { ["revisionId"] = revision.ProductConfigurationRevisionId.Value });
            ListingConfigurationValidator.ValidateForPublication(
                listing,
                revision,
                configuration,
                _timeProvider.GetUtcNow());
        }

        var expectedStoredRevision = listing.AggregateRevision;
        mutation(listing, expectedAggregateRevision, actorId, _timeProvider.GetUtcNow());
        var commandDigest = CatalogCanonicalJson.ComputeDigest(new LifecycleCanonical(
            listingId.Value,
            expectedAggregateRevision,
            operation,
            request.Reason,
            actorId.Value));
        var command = CatalogCommandIdentity.Create(
            $"listing/{operation}/{listingId.Value:D}",
            idempotencyKey,
            commandDigest);
        var result = await _listingStore.SaveLifecycleAsync(
            listing,
            expectedStoredRevision,
            operation,
            request.Reason,
            command,
            cancellationToken);
        return CatalogContractMapper.ToDto(result.Value);
    }

    private async Task<Listing> GetRequiredListingAsync(ListingId listingId, CancellationToken cancellationToken) =>
        await _listingStore.GetAsync(listingId, cancellationToken)
            ?? throw NotFound(
                "LISTING_NOT_FOUND",
                "The listing does not exist.",
                new Dictionary<string, object?> { ["listingId"] = listingId.Value });

    private static CreateListingRevisionRequest Normalize(CreateListingRevisionRequest request) =>
        request with
        {
            Translations = request.Translations.OrderBy(item => item.Locale, StringComparer.Ordinal).ToArray(),
            CategoryKeys = request.CategoryKeys.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
            Attributes = request.Attributes.OrderBy(item => item.AttributeKey, StringComparer.Ordinal).ToArray(),
            Provenance = request.Provenance
                .OrderBy(item => item.FieldPath, StringComparer.Ordinal)
                .ThenBy(item => item.SourceKind, StringComparer.Ordinal)
                .ThenBy(item => item.SourceReference, StringComparer.Ordinal)
                .ToArray(),
        };

    private static void ValidateRevisionRequestCollections(CreateListingRevisionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request.Translations);
        ArgumentNullException.ThrowIfNull(request.CategoryKeys);
        ArgumentNullException.ThrowIfNull(request.Attributes);
        ArgumentNullException.ThrowIfNull(request.Provenance);
        EnsureIdentifier(request.SubjectRevisionId, nameof(request.SubjectRevisionId));
        EnsureIdentifier(request.ProductConfigurationRevisionId, nameof(request.ProductConfigurationRevisionId));
        EnsureIdentifier(request.TaxonomyRevisionId, nameof(request.TaxonomyRevisionId));
        EnsureIdentifier(request.AttributeRevisionId, nameof(request.AttributeRevisionId));
        EnsureIdentifier(request.MarketAreaRevisionId, nameof(request.MarketAreaRevisionId));
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new CatalogCommandException(
                "Catalog.Contracts",
                "IDENTIFIER_REQUIRED",
                400,
                $"'{parameterName}' must be a non-empty UUID.",
                "Provide the exact owner identity in UUID form.");
        }
    }

    private static void EnsureExpectedRevision(long value)
    {
        if (value < 1)
        {
            throw new CatalogCommandException(
                "Catalog.Listings",
                "EXPECTED_REVISION_INVALID",
                400,
                "Expected aggregate revision must be positive.",
                "Send the exact current aggregate revision through If-Match.");
        }
    }

    private static void RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1_000)
        {
            throw new CatalogCommandException(
                "Catalog.Listings",
                "EDITORIAL_REASON_INVALID",
                400,
                "Editorial reason must contain between 1 and 1000 characters.",
                "Provide a concise auditable reason.");
        }
    }

    private static CatalogCommandException NotFound(
        string code,
        string message,
        IReadOnlyDictionary<string, object?> context) =>
        new("Catalog.Listings", code, 404, message, "Use an existing exact Catalog identity.", context);

    private static CatalogCommandException UnsupportedListingKind(ListingKindContract kind) =>
        new(
            "Catalog.Contracts",
            "LISTING_KIND_UNSUPPORTED",
            400,
            $"Listing kind '{kind}' is not supported.",
            "Use one of the listing kinds declared by the current Catalog contract.");

    private sealed record ListingRevisionCanonical(Guid ListingId, CreateListingRevisionRequest Revision);

    private sealed record LifecycleCanonical(
        Guid ListingId,
        long ExpectedAggregateRevision,
        string Operation,
        string Reason,
        Guid ActorId);
}
