using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>Creates application-owned business identifiers.</summary>
public interface ICatalogIdSource
{
    public Guid CreateId();
}

/// <summary>Persists Catalog aggregates and their transactional outbox effects.</summary>
public interface ICatalogRepository
{
    public Task AddConfigurationAsync(
        ProductConfiguration configuration,
        byte[] canonicalDocument,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken);

    public Task<ProductConfiguration?> GetConfigurationAsync(
        Guid configurationRevisionId,
        CancellationToken cancellationToken);

    public Task<ProductConfiguration?> GetActiveConfigurationAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken);

    public Task ActivateConfigurationAsync(
        CatalogKey catalogKey,
        Guid configurationRevisionId,
        Guid expectedConfigurationRevisionId,
        Guid actorId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken);

    public Task AddListingAsync(Listing listing, CancellationToken cancellationToken);

    public Task<Listing?> GetListingAsync(Guid listingId, CancellationToken cancellationToken);

    public Task<ListingRevision?> GetListingRevisionAsync(
        Guid revisionId,
        CancellationToken cancellationToken);

    public Task AddListingRevisionAsync(
        Listing listing,
        ListingRevision revision,
        CancellationToken cancellationToken);

    public Task AddEditorialDecisionAsync(
        Listing listing,
        EditorialDecision decision,
        CancellationToken cancellationToken);

    public Task ArchiveListingAsync(Listing listing, CancellationToken cancellationToken);

    public Task<IReadOnlyList<PublicationSelectionState>> GetPublicationSelectionsAsync(
        CatalogKey catalogKey,
        IReadOnlyList<PublicationSelectionContract> selections,
        CancellationToken cancellationToken);

    public Task<long> GetNextPublicationSequenceAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken);

    public Task<long> GetNextPublicationActivationRevisionAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken);

    public Task<Guid?> GetCurrentPublicationIdAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken);

    public Task<CatalogPublication?> GetPublicationAsync(
        Guid publicationId,
        CancellationToken cancellationToken);

    public Task CommitPublicationAsync(
        CatalogPublication publication,
        Guid? expectedCurrentPublicationId,
        IReadOnlyList<Listing> listings,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    public Task ActivateExistingPublicationAsync(
        CatalogPublication targetPublication,
        Guid expectedCurrentPublicationId,
        CurrentPublicationPointer publicationPointer,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    public Task AddClaimAsync(ListingClaim claim, CancellationToken cancellationToken);

    public Task<ListingClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken);

    public Task CompleteClaimVerificationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    public Task SaveClaimDecisionAsync(
        ListingClaim claim,
        CatalogOutboxMessage? outboxMessage,
        CancellationToken cancellationToken);
}

/// <summary>Stores one immutable publication artifact and proves its exact digest before Catalog activation.</summary>
public interface ICatalogPublicationArtifactStore
{
    public Task PutVerifiedAsync(
        string objectKey,
        ReadOnlyMemory<byte> content,
        string sha256Digest,
        CancellationToken cancellationToken);
}

public sealed record PublicationSelectionState(
    Listing Listing,
    ListingRevision Revision);

/// <summary>Producer-owned event write persisted atomically with the Catalog business transition.</summary>
public sealed record CatalogOutboxMessage(
    Guid Id,
    string EventType,
    string ContractIdentity,
    string Payload,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId);

public sealed record CatalogActor(Guid Id)
{
    public static CatalogActor Create(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Actor ID is required.", nameof(id));
        }

        return new CatalogActor(id);
    }
}

public sealed class CatalogNotFoundException : InvalidOperationException
{
    public CatalogNotFoundException(string resourceType, object resourceId)
        : base(CreateMessage(resourceType, resourceId))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentNullException.ThrowIfNull(resourceId);
        ResourceType = resourceType.Trim();
        ResourceId = resourceId.ToString()
            ?? throw new ArgumentException("Resource ID cannot be rendered.", nameof(resourceId));
    }

    public string ResourceType { get; }

    public string ResourceId { get; }

    private static string CreateMessage(string resourceType, object resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceType);
        ArgumentNullException.ThrowIfNull(resourceId);
        return $"{resourceType.Trim()} '{resourceId}' was not found.";
    }
}

public sealed class CatalogConflictException : InvalidOperationException
{
    public CatalogConflictException(string message)
        : base(message)
    {
    }
}

public sealed class CatalogContractException : InvalidOperationException
{
    public CatalogContractException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
