using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogPublicationRollbackTests
{
    private static readonly Guid CurrentPublicationId =
        Guid.Parse("0198fd00-0000-7000-8000-000000000001");
    private static readonly Guid TargetPublicationId =
        Guid.Parse("0198fd00-0000-7000-8000-000000000002");
    private static readonly Guid ConfigurationRevisionId =
        Guid.Parse("0198fd00-0000-7000-8000-000000000003");
    private static readonly Guid ActorId =
        Guid.Parse("0198fd00-0000-7000-8000-000000000004");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 15, 50, 0, TimeSpan.Zero);
    private const string ArtifactDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string ArtifactKey =
        "catalog/berlin-recording-services/publications/0198fd00000070008000000000000002.json";

    [Fact]
    public async Task ArtifactVerificationFailureLeavesCurrentPointerAndOutboxUntouched()
    {
        var target = CreateTargetPublication();
        var repository = new RollbackRepository(target, CurrentPublicationId);
        var artifactStore = new FailingArtifactStore();
        var service = new CatalogPublicationService(
            repository,
            artifactStore,
            new UnexpectedIdSource(),
            new FixedTimeProvider(Timestamp));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RollbackAsync(
            "berlin-recording-services",
            new RollbackPublicationRequest(TargetPublicationId, CurrentPublicationId),
            CatalogActor.Create(ActorId),
            CancellationToken.None));

        Assert.Equal("The exact rollback artifact is unavailable.", exception.Message);
        Assert.Equal(ArtifactKey, artifactStore.VerifiedObjectKey);
        Assert.Equal(ArtifactDigest, artifactStore.VerifiedDigest);
        Assert.Equal(CurrentPublicationId, repository.CurrentPublicationId);
        Assert.Equal(0, repository.ActivationAttempts);
        Assert.Equal(0, repository.ActivationRevisionRequests);
    }

    private static CatalogPublication CreateTargetPublication() =>
        CatalogPublication.Create(
            TargetPublicationId,
            CatalogKey.Create("berlin-recording-services"),
            ConfigurationRevisionId,
            sequence: 1,
            ArtifactKey,
            ArtifactDigest,
            [
                PublicationEntry.Create(
                    Guid.Parse("0198fd00-0000-7000-8000-000000000010"),
                    Guid.Parse("0198fd00-0000-7000-8000-000000000011"),
                    Guid.Parse("0198fd00-0000-7000-8000-000000000012"),
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"),
            ],
            ActorId,
            Timestamp);

    private sealed class FailingArtifactStore : ICatalogPublicationArtifactStore
    {
        public string? VerifiedObjectKey { get; private set; }

        public string? VerifiedDigest { get; private set; }

        public Task PutVerifiedAsync(
            string objectKey,
            ReadOnlyMemory<byte> content,
            string sha256Digest,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Publication creation is outside this rollback test.");

        public Task VerifyAsync(
            string objectKey,
            string sha256Digest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VerifiedObjectKey = objectKey;
            VerifiedDigest = sha256Digest;
            throw new InvalidOperationException("The exact rollback artifact is unavailable.");
        }
    }

    private sealed class UnexpectedIdSource : ICatalogIdSource
    {
        public Guid CreateId() =>
            throw new InvalidOperationException("Rollback must not allocate an event ID before artifact verification.");
    }

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class RollbackRepository(
        CatalogPublication targetPublication,
        Guid currentPublicationId) : ICatalogRepository
    {
        public Guid? CurrentPublicationId { get; private set; } = currentPublicationId;

        public int ActivationAttempts { get; private set; }

        public int ActivationRevisionRequests { get; private set; }

        public Task<Guid?> GetCurrentPublicationIdAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCatalog(catalogKey);
            return Task.FromResult(CurrentPublicationId);
        }

        public Task<CatalogPublication?> GetPublicationAsync(
            Guid publicationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<CatalogPublication?>(
                publicationId == targetPublication.Id ? targetPublication : null);
        }

        public Task<long> GetNextPublicationActivationRevisionAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCatalog(catalogKey);
            ActivationRevisionRequests++;
            return Task.FromResult(1L);
        }

        public Task ActivateExistingPublicationAsync(
            CatalogPublication target,
            Guid expectedCurrentPublicationId,
            CurrentPublicationPointer publicationPointer,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivationAttempts++;
            CurrentPublicationId = target.Id;
            return Task.CompletedTask;
        }

        public Task AddConfigurationAsync(
            ProductConfiguration configuration,
            byte[] canonicalDocument,
            DateTimeOffset importedAtUtc,
            CancellationToken cancellationToken) =>
            Unsupported();

        public Task<ProductConfiguration?> GetConfigurationAsync(
            Guid configurationRevisionId,
            CancellationToken cancellationToken) =>
            Unsupported<ProductConfiguration?>();

        public Task<ProductConfiguration?> GetActiveConfigurationAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken) =>
            Unsupported<ProductConfiguration?>();

        public Task ActivateConfigurationAsync(
            CatalogKey catalogKey,
            Guid configurationRevisionId,
            Guid expectedConfigurationRevisionId,
            Guid actorId,
            DateTimeOffset activatedAtUtc,
            CancellationToken cancellationToken) =>
            Unsupported();

        public Task AddListingAsync(Listing listing, CancellationToken cancellationToken) =>
            Unsupported();

        public Task<Listing?> GetListingAsync(Guid listingId, CancellationToken cancellationToken) =>
            Unsupported<Listing?>();

        public Task<ListingRevision?> GetListingRevisionAsync(
            Guid revisionId,
            CancellationToken cancellationToken) =>
            Unsupported<ListingRevision?>();

        public Task AddListingRevisionAsync(
            Listing listing,
            ListingRevision revision,
            CancellationToken cancellationToken) =>
            Unsupported();

        public Task AddEditorialDecisionAsync(
            Listing listing,
            EditorialDecision decision,
            CancellationToken cancellationToken) =>
            Unsupported();

        public Task ArchiveListingAsync(Listing listing, CancellationToken cancellationToken) =>
            Unsupported();

        public Task<IReadOnlyList<PublicationSelectionState>> GetPublicationSelectionsAsync(
            CatalogKey catalogKey,
            IReadOnlyList<PublicationSelectionContract> selections,
            CancellationToken cancellationToken) =>
            Unsupported<IReadOnlyList<PublicationSelectionState>>();

        public Task<long> GetNextPublicationSequenceAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken) =>
            Unsupported<long>();

        public Task CommitPublicationAsync(
            CatalogPublication publication,
            Guid? expectedCurrentPublicationId,
            IReadOnlyList<Listing> listings,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            Unsupported();

        public Task AddClaimAsync(ListingClaim claim, CancellationToken cancellationToken) =>
            Unsupported();

        public Task<ListingClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken) =>
            Unsupported<ListingClaim?>();

        public Task CompleteClaimVerificationAsync(
            ListingClaim claim,
            ListingAccessGrant grant,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            Unsupported();

        public Task SaveClaimDecisionAsync(
            ListingClaim claim,
            CatalogOutboxMessage? outboxMessage,
            CancellationToken cancellationToken) =>
            Unsupported();

        private void EnsureCatalog(CatalogKey catalogKey)
        {
            if (catalogKey != targetPublication.CatalogKey)
            {
                throw new InvalidOperationException("The rollback test received an unexpected catalog.");
            }
        }

        private static Task Unsupported() =>
            throw new NotSupportedException("The operation is outside this rollback test.");

        private static Task<T> Unsupported<T>() =>
            throw new NotSupportedException("The operation is outside this rollback test.");
    }
}
