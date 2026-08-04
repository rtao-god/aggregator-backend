using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public interface ICatalogIdSource
{
    Guid Next();
}

public interface ICatalogRepository
{
    Task AddConfigurationAsync(
        ProductConfiguration configuration,
        byte[] canonicalDocument,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken);

    Task<ProductConfiguration?> GetConfigurationAsync(
        Guid configurationRevisionId,
        CancellationToken cancellationToken);

    Task<ProductConfiguration?> GetActiveConfigurationAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken);

    Task ActivateConfigurationAsync(
        CatalogKey catalogKey,
        Guid configurationRevisionId,
        Guid expectedConfigurationRevisionId,
        Guid actorId,
        DateTimeOffset activatedAtUtc,
        CancellationToken cancellationToken);

    Task AddListingAsync(Listing listing, CancellationToken cancellationToken);

    Task<Listing?> GetListingAsync(Guid listingId, CancellationToken cancellationToken);

    Task<ListingRevision?> GetListingRevisionAsync(Guid revisionId, CancellationToken cancellationToken);

    Task AddListingRevisionAsync(
        Listing listing,
        ListingRevision revision,
        CancellationToken cancellationToken);

    Task AddEditorialDecisionAsync(
        Listing listing,
        EditorialDecision decision,
        CancellationToken cancellationToken);

    Task ArchiveListingAsync(Listing listing, CancellationToken cancellationToken);

    Task<IReadOnlyList<PublicationSelectionState>> GetPublicationSelectionsAsync(
        CatalogKey catalogKey,
        IReadOnlyList<PublicationSelectionContract> selections,
        CancellationToken cancellationToken);

    Task<long> GetNextPublicationSequenceAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken);

    Task<Guid?> GetCurrentPublicationIdAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken);

    Task<CatalogPublication?> GetPublicationAsync(
        Guid publicationId,
        CancellationToken cancellationToken);

    Task CommitPublicationAsync(
        CatalogPublication publication,
        Guid? expectedCurrentPublicationId,
        IReadOnlyList<Listing> listings,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    Task ActivateExistingPublicationAsync(
        CatalogPublication targetPublication,
        Guid expectedCurrentPublicationId,
        CurrentPublicationPointer pointer,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    Task AddClaimAsync(ListingClaim claim, CancellationToken cancellationToken);

    Task<ListingClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken);

    Task CompleteClaimVerificationAsync(
        ListingClaim claim,
        ListingAccessGrant grant,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    Task SaveClaimDecisionAsync(
        ListingClaim claim,
        CatalogOutboxMessage? outboxMessage,
        CancellationToken cancellationToken);
}

public interface ICatalogPublicationArtifactStore
{
    Task PutVerifiedAsync(
        string objectKey,
        ReadOnlyMemory<byte> content,
        string sha256Digest,
        CancellationToken cancellationToken);
}

public sealed record PublicationSelectionState(
    Listing Listing,
    ListingRevision Revision);

public sealed record CatalogOutboxMessage(
    Guid Id,
    string EventType,
    int EventRevision,
    string Payload,
    DateTimeOffset OccurredAtUtc);

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
        : base($"{resourceType} '{resourceId}' was not found.")
    {
        ResourceType = resourceType;
        ResourceId = resourceId.ToString() ?? throw new ArgumentException("Resource ID cannot be rendered.", nameof(resourceId));
    }

    public string ResourceType { get; }

    public string ResourceId { get; }
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
