using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed record ProductConfigurationRevisionEnvelope(
    ProductConfigurationRevision Revision,
    CatalogId CatalogId,
    TaxonomyRevisionId TaxonomyRevisionId,
    AttributeRevisionId AttributeRevisionId,
    MarketAreaRevisionId MarketAreaRevisionId,
    string CanonicalSnapshot,
    bool Active);

public sealed record ProductConfigurationActivationRecord(
    CatalogId CatalogId,
    ProductConfigurationRevisionId RevisionId,
    ProductConfigurationRevisionId? PreviousRevisionId,
    ActorId ActivatedBy,
    DateTimeOffset ActivatedAtUtc,
    string Reason);

public interface IProductConfigurationStore
{
    public Task<CommandPersistenceResult<ProductConfigurationRevisionEnvelope>> SaveRevisionAsync(
        ProductConfigurationRevisionEnvelope revision,
        CatalogCommandIdentity command,
        CancellationToken cancellationToken);

    public Task<ProductConfigurationRevisionEnvelope?> GetRevisionAsync(
        ProductConfigurationRevisionId revisionId,
        CancellationToken cancellationToken);

    public Task<ProductConfigurationRevisionEnvelope?> GetActiveRevisionAsync(
        CatalogId catalogId,
        CancellationToken cancellationToken);

    public Task<CommandPersistenceResult<ProductConfigurationActivationRecord>> ActivateAsync(
        CatalogId catalogId,
        ProductConfigurationRevisionId revisionId,
        ProductConfigurationRevisionId? expectedActiveRevisionId,
        ActorId actorId,
        DateTimeOffset activatedAtUtc,
        string reason,
        CatalogCommandIdentity command,
        CancellationToken cancellationToken);
}

public sealed record ListingRevisionWriteResult(Listing Listing, ListingRevision Revision);

public interface IListingStore
{
    public Task<CommandPersistenceResult<Listing>> CreateAsync(
        Listing listing,
        CatalogCommandIdentity command,
        CancellationToken cancellationToken);

    public Task<Listing?> GetAsync(ListingId listingId, CancellationToken cancellationToken);

    public Task<ListingRevision?> GetRevisionAsync(
        ListingRevisionId revisionId,
        CancellationToken cancellationToken);

    public Task<CommandPersistenceResult<ListingRevisionWriteResult>> SaveRevisionAsync(
        Listing listing,
        ListingRevision revision,
        long expectedStoredAggregateRevision,
        string reason,
        CatalogCommandIdentity command,
        CancellationToken cancellationToken);

    public Task<CommandPersistenceResult<Listing>> SaveLifecycleAsync(
        Listing listing,
        long expectedStoredAggregateRevision,
        string operation,
        string reason,
        CatalogCommandIdentity command,
        CancellationToken cancellationToken);
}

public sealed record PublicationListingSource(Listing Listing, ListingRevision Revision);

public sealed record StoredPublicationArtifact(
    string ObjectKey,
    string ContentDigest,
    long Size,
    string SchemaIdentity);

public interface IPublicationArtifactStore
{
    public Task<StoredPublicationArtifact> PutVerifiedAsync(
        PublicationId publicationId,
        ReadOnlyMemory<byte> content,
        string expectedDigest,
        string schemaIdentity,
        CancellationToken cancellationToken);
}

public sealed record ClaimedPublicationRequest(
    CatalogPublicationRequest Request,
    string CorrelationId,
    Guid? CausationId);

public sealed record PublicationCompletion(
    CatalogPublicationRequest Request,
    CatalogPublication Publication,
    StoredPublicationArtifact Artifact,
    IReadOnlyList<Listing> ActivatedListings,
    string EventPayloadJson,
    string EventPayloadDigest,
    string EventRoutingKey,
    string EventContractIdentity,
    Guid EventMessageId,
    string CorrelationId,
    Guid? CausationId);

public interface IPublicationStore
{
    public Task<PublicationId?> GetCurrentPublicationIdAsync(
        CatalogId catalogId,
        CancellationToken cancellationToken);

    public Task<CommandPersistenceResult<CatalogPublicationRequest>> CreateRequestAsync(
        CatalogPublicationRequest request,
        IReadOnlyList<Listing> listings,
        IReadOnlyDictionary<ListingId, long> expectedStoredAggregateRevisions,
        CatalogCommandIdentity command,
        string correlationId,
        CancellationToken cancellationToken);

    public Task<ClaimedPublicationRequest?> ClaimNextAsync(
        string workerIdentity,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken);

    public Task<IReadOnlyList<PublicationListingSource>> LoadSourcesAsync(
        CatalogPublicationRequest request,
        CancellationToken cancellationToken);

    public Task CompleteAsync(PublicationCompletion completion, CancellationToken cancellationToken);

    public Task FailAsync(
        CatalogPublicationRequest request,
        string failureCode,
        string failureDetail,
        CancellationToken cancellationToken);
}
