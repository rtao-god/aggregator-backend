using System.Security.Cryptography;
using System.Text;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Media.Contracts;

namespace Catalog.Application.Tests;

public sealed class CatalogEndToEndTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConfigurationRevisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000001");
    private static readonly Guid SubjectId = Guid.Parse("0192f5f0-0000-7000-8000-000000000002");
    private static readonly Guid SubjectRevisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000003");
    private static readonly Guid ActorId = Guid.Parse("0192f5f0-0000-7000-8000-000000000004");
    private const string ConfigurationDigest = "ac8b665d754ac21f0ec66fab729e1a669de53effaed4120a6e004dc9f7785f31";

    [Fact]
    public async Task CatalogFlowImportsConfigurationPublishesAndRollsBackExactRevisions()
    {
        var identifiers = Enumerable.Range(10, 20)
            .Select(number => Guid.Parse($"0192f5f0-0000-7000-8000-{number:D12}"))
            .ToArray();
        var repository = new InMemoryCatalogRepository();
        var artifactStore = new VerifyingArtifactStore();
        var idSource = new QueueCatalogIdSource(identifiers);
        var timeProvider = new FixedTimeProvider(Timestamp);
        var actor = CatalogActor.Create(ActorId);
        var configurationService = new CatalogConfigurationService(repository, timeProvider);
        var listingService = new CatalogListingService(
            repository,
            new UnexpectedMediaBindingAuthority(),
            idSource,
            timeProvider);
        var publicationService = new CatalogPublicationService(repository, artifactStore, idSource, timeProvider);

        var imported = await configurationService.ImportAsync(
            new ImportProductConfigurationRequest(
                CatalogContractIdentity.ProductConfiguration,
                CatalogContractIdentity.ProductConfigurationRevision,
                ConfigurationDigest,
                CreateConfiguration()),
            CancellationToken.None);
        Assert.Equal(ConfigurationDigest, imported.ContentDigest);
        Assert.False(imported.IsActive);

        var activated = await configurationService.ActivateAsync(
            "berlin-recording-services",
            new ActivateProductConfigurationRequest(
                ConfigurationRevisionId,
                new ConfigurationPointerExpectationContract(
                    PointerExpectationKindContract.Absent,
                    ConfigurationRevisionId: null)),
            actor,
            CancellationToken.None);
        Assert.True(activated.IsActive);

        var listing = await listingService.CreateAsync(
            new CreateListingRequest(
                "berlin-recording-services",
                new SubjectReferenceContract(SubjectId, SubjectRevisionId, SubjectKindContract.Place)),
            actor,
            CancellationToken.None);
        var firstRevision = await listingService.AddRevisionAsync(
            listing.Id,
            new CreateListingRevisionRequest(
                ExpectedVersion: 1,
                ConfigurationRevisionId,
                listing.Subject,
                CreateContent(80m, PriceAssertion(1))),
            actor,
            CancellationToken.None);
        _ = await listingService.ApproveAsync(
            listing.Id,
            new ApproveListingRevisionRequest(ExpectedVersion: 2, RevisionId: firstRevision.Id),
            actor,
            CancellationToken.None);
        var firstPublication = await publicationService.PublishAsync(
            new CreateCatalogPublicationRequest(
                "berlin-recording-services",
                ConfigurationRevisionId,
                new PublicationPointerExpectationContract(
                    PointerExpectationKindContract.Absent,
                    PublicationId: null),
                [new PublicationSelectionContract(listing.Id, firstRevision.Id, ExpectedListingVersion: 3)]),
            actor,
            CancellationToken.None);

        var secondRevision = await listingService.AddRevisionAsync(
            listing.Id,
            new CreateListingRevisionRequest(
                ExpectedVersion: 4,
                ConfigurationRevisionId,
                listing.Subject,
                CreateContent(95m, PriceAssertion(2))),
            actor,
            CancellationToken.None);
        _ = await listingService.ApproveAsync(
            listing.Id,
            new ApproveListingRevisionRequest(ExpectedVersion: 5, RevisionId: secondRevision.Id),
            actor,
            CancellationToken.None);
        var secondPublication = await publicationService.PublishAsync(
            new CreateCatalogPublicationRequest(
                "berlin-recording-services",
                ConfigurationRevisionId,
                new PublicationPointerExpectationContract(
                    PointerExpectationKindContract.Exact,
                    firstPublication.Id),
                [new PublicationSelectionContract(listing.Id, secondRevision.Id, ExpectedListingVersion: 6)]),
            actor,
            CancellationToken.None);
        var rollback = await publicationService.RollbackAsync(
            "berlin-recording-services",
            new RollbackPublicationRequest(firstPublication.Id, secondPublication.Id),
            actor,
            CancellationToken.None);

        Assert.Equal(identifiers[1], firstRevision.Id);
        Assert.Equal(firstPublication.Id, rollback.Id);
        Assert.Equal(firstPublication.Id, repository.CurrentPublicationId);
        Assert.Equal(2, artifactStore.Artifacts.Count);
        var verification = Assert.Single(artifactStore.Verifications);
        Assert.Equal(firstPublication.ArtifactKey, verification.ObjectKey);
        Assert.Equal(firstPublication.ArtifactDigest, verification.Digest);
        var firstArtifactJson = Encoding.UTF8.GetString(
            artifactStore.Artifacts[firstPublication.ArtifactKey]);
        Assert.Contains("\"contractRevision\":4", firstArtifactJson, StringComparison.Ordinal);
        Assert.Contains(
            $"\"contactId\":\"{identifiers[2]:D}\"",
            firstArtifactJson,
            StringComparison.Ordinal);
        Assert.Equal(3, repository.OutboxMessages.Count);
        Assert.Equal(4, repository.NextPublicationActivationRevision);
        Assert.All(
            repository.OutboxMessages,
            message => Assert.Equal(CatalogIntegrationEventTypes.PublicationActivated, message.EventType));
    }

    private static ProductConfigurationContract CreateConfiguration() =>
        new(
            ConfigurationRevisionId,
            Timestamp,
            new SiteDefinitionContract(
                "berlin-recording",
                "de-DE",
                ["en-GB", "de-DE"],
                "EUR",
                "Europe/Berlin"),
            new CatalogDefinitionContract(
                "berlin-recording-services",
                "berlin-recording",
                "berlin-core-and-nearby",
                "EUR",
                "Europe/Berlin",
                [SubjectKindContract.Provider, SubjectKindContract.Place]),
            [
                Category(
                    "recording-studio",
                    SubjectKindContract.Place,
                    "Tonstudio",
                    "Recording studio"),
                Category(
                    "music-producer",
                    SubjectKindContract.Provider,
                    "Musikproduzent",
                    "Music producer"),
            ],
            [
                new AttributeDefinitionContract(
                    "hourly-price",
                    AttributeValueKindContract.Decimal,
                    AttributeCardinalityContract.Single,
                    PublicFieldRequirementContract.Optional,
                    ["recording-studio"],
                    Localized("Stundenpreis", "Hourly price"),
                    Minimum: null,
                    Maximum: null,
                    AllowedValues: [],
                    IsFilterable: true,
                    IsSortable: true),
            ]);

    private static CategoryDefinitionContract Category(
        string key,
        SubjectKindContract subjectKind,
        string german,
        string english) =>
        new(key, [subjectKind], Localized(german, english), IsActive: true);

    private static Dictionary<string, string> Localized(string german, string english) =>
        new(StringComparer.Ordinal)
        {
            ["de-DE"] = german,
            ["en-GB"] = english,
        };

    private static ListingRevisionContentContract CreateContent(decimal hourlyPrice, Guid priceAssertionId)
    {
        var nameAssertionId = Assertion(20);
        var categoryAssertionId = Assertion(21);
        var geographyAssertionId = Assertion(22);
        var contactAssertionId = Assertion(23);
        return new ListingRevisionContentContract(
            [
                new LocalizedTextValueContract(
                    "de-DE",
                    FieldValueStateContract.Observed,
                    "Beispiel Tonstudio",
                    nameAssertionId,
                    MissingReason: null),
                new LocalizedTextValueContract(
                    "en-GB",
                    FieldValueStateContract.Missing,
                    Value: null,
                    AssertionId: null,
                    MissingValueReasonContract.NotPublishedBySource),
            ],
            [
                new LocalizedTextValueContract(
                    "de-DE",
                    FieldValueStateContract.Missing,
                    Value: null,
                    AssertionId: null,
                    MissingValueReasonContract.NotPublishedBySource),
            ],
            [new CategoryAssignmentContract("recording-studio", categoryAssertionId)],
            [
                new ListingAttributeValueContract(
                    "hourly-price",
                    FieldValueStateContract.Observed,
                    new TypedValueContract(
                        AttributeValueKindContract.Decimal,
                        BooleanValue: null,
                        DecimalValue: hourlyPrice,
                        TextValue: null,
                        TextSetValue: null),
                    priceAssertionId,
                    MissingReason: null),
            ],
            new GeographyValueContract(
                GeographyStateContract.PrimaryMarket,
                Latitude: 52.520008m,
                Longitude: 13.404954m,
                DistrictKey: "mitte",
                geographyAssertionId),
            [
                new ContactValueContract(
                    ContactKindContract.Website,
                    "https://example.test/studio",
                    Label: null,
                    contactAssertionId),
            ],
            Media: [],
            Assertions:
            [
                CreateAssertion(nameAssertionId, "name"),
                CreateAssertion(categoryAssertionId, "category"),
                CreateAssertion(geographyAssertionId, "geography"),
                CreateAssertion(contactAssertionId, "contact"),
                CreateAssertion(priceAssertionId, "price"),
            ]);
    }

    private static Guid Assertion(int suffix) =>
        Guid.Parse($"0192f5f0-0000-7000-8000-{suffix:D12}");

    private static Guid PriceAssertion(int revision) => Assertion(24 + revision);

    private static ProvenanceAssertionContract CreateAssertion(Guid id, string sourceReference) =>
        new(
            id,
            SourceKindContract.FirstPartySubmission,
            sourceReference,
            Timestamp,
            Timestamp,
            UsagePolicyContract.PublicAllowed,
            Convert.ToHexString(SHA256.HashData(id.ToByteArray())).ToLowerInvariant());

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class QueueCatalogIdSource(IEnumerable<Guid> values) : ICatalogIdSource
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid CreateId() =>
            _values.Count > 0
                ? _values.Dequeue()
                : throw new InvalidOperationException("The test ID sequence is exhausted.");
    }

    private sealed class VerifyingArtifactStore : ICatalogPublicationArtifactStore
    {
        public Dictionary<string, byte[]> Artifacts { get; } = new(StringComparer.Ordinal);

        public List<(string ObjectKey, string Digest)> Verifications { get; } = [];

        public Task PutVerifiedAsync(
            string objectKey,
            ReadOnlyMemory<byte> content,
            string sha256Digest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(
                sha256Digest,
                Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant());
            Assert.True(Artifacts.TryAdd(objectKey, content.ToArray()));
            return Task.CompletedTask;
        }

        public Task VerifyAsync(
            string objectKey,
            string sha256Digest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Artifacts.TryGetValue(objectKey, out var content))
            {
                throw new InvalidOperationException($"Artifact '{objectKey}' does not exist.");
            }

            Assert.Equal(
                sha256Digest,
                Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
            Verifications.Add((objectKey, sha256Digest));
            return Task.CompletedTask;
        }
    }

    private sealed class UnexpectedMediaBindingAuthority : ICatalogMediaPublicationBindingAuthority
    {
        public Task<CatalogMediaPublicationBindingContract> RequirePublishableBindingAsync(
            string catalogKey,
            Guid mediaId,
            long expectedMediaAggregateRevision,
            Guid variantId,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The media authority must not be called for a revision without media references.");
    }

    private sealed class InMemoryCatalogRepository : ICatalogRepository
    {
        private readonly Dictionary<Guid, ProductConfiguration> _configurations = [];
        private readonly Dictionary<string, Guid> _activeConfigurations = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Listing> _listings = [];
        private readonly Dictionary<Guid, ListingRevision> _revisions = [];
        private readonly Dictionary<Guid, CatalogPublication> _publications = [];
        private long _nextPublicationSequence = 1;
        private long _nextPublicationActivationRevision = 1;

        public Guid? CurrentPublicationId { get; private set; }

        public long NextPublicationActivationRevision => _nextPublicationActivationRevision;

        public List<CatalogOutboxMessage> OutboxMessages { get; } = [];

        public Task AddConfigurationAsync(
            ProductConfiguration configuration,
            byte[] canonicalDocument,
            DateTimeOffset importedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotEmpty(canonicalDocument);
            Assert.True(_configurations.TryAdd(configuration.RevisionId, configuration));
            return Task.CompletedTask;
        }

        public Task<ProductConfiguration?> GetConfigurationAsync(
            Guid configurationRevisionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _configurations.TryGetValue(configurationRevisionId, out var value);
            return Task.FromResult(value);
        }

        public Task<ProductConfiguration?> GetActiveConfigurationAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                _activeConfigurations.TryGetValue(catalogKey.Value, out var revisionId)
                    ? _configurations[revisionId]
                    : null);
        }

        public Task ActivateConfigurationAsync(
            CatalogKey catalogKey,
            Guid configurationRevisionId,
            Guid expectedConfigurationRevisionId,
            Guid actorId,
            DateTimeOffset activatedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = _activeConfigurations.TryGetValue(catalogKey.Value, out var revisionId)
                ? revisionId
                : Guid.Empty;
            if (actual != expectedConfigurationRevisionId)
            {
                throw new CatalogConflictException("Configuration pointer mismatch.");
            }

            _activeConfigurations[catalogKey.Value] = configurationRevisionId;
            return Task.CompletedTask;
        }

        public Task AddListingAsync(Listing listing, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(_listings.TryAdd(listing.Id, listing));
            return Task.CompletedTask;
        }

        public Task<Listing?> GetListingAsync(Guid listingId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _listings.TryGetValue(listingId, out var value);
            return Task.FromResult(value);
        }

        public Task<ListingRevision?> GetListingRevisionAsync(
            Guid revisionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _revisions.TryGetValue(revisionId, out var value);
            return Task.FromResult(value);
        }

        public Task AddListingRevisionAsync(
            Listing listing,
            ListingRevision revision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _listings[listing.Id] = listing;
            Assert.True(_revisions.TryAdd(revision.Id, revision));
            return Task.CompletedTask;
        }

        public Task AddEditorialDecisionAsync(
            Listing listing,
            EditorialDecision decision,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(listing.Id, decision.ListingId);
            _listings[listing.Id] = listing;
            return Task.CompletedTask;
        }

        public Task ArchiveListingAsync(Listing listing, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _listings[listing.Id] = listing;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PublicationSelectionState>> GetPublicationSelectionsAsync(
            CatalogKey catalogKey,
            IReadOnlyList<PublicationSelectionContract> selections,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PublicationSelectionState> values = selections
                .Select(selection => new PublicationSelectionState(
                    _listings[selection.ListingId],
                    _revisions[selection.ListingRevisionId]))
                .Where(selection => selection.Listing.CatalogKey == catalogKey)
                .ToArray();
            return Task.FromResult(values);
        }

        public Task<long> GetNextPublicationSequenceAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_nextPublicationSequence++);
        }

        public Task<Guid?> GetCurrentPublicationIdAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentPublicationId);
        }

        public Task<CatalogPublication?> GetPublicationAsync(
            Guid publicationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _publications.TryGetValue(publicationId, out var value);
            return Task.FromResult(value);
        }

        public Task CommitPublicationAsync(
            CatalogPublication publication,
            Guid? expectedCurrentPublicationId,
            IReadOnlyList<Listing> listings,
            CatalogPublicationActivationOutboxFactory outboxFactory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePointer(expectedCurrentPublicationId);
            var outboxMessage = outboxFactory(_nextPublicationActivationRevision);
            Assert.True(_publications.TryAdd(publication.Id, publication));
            CurrentPublicationId = publication.Id;
            OutboxMessages.Add(outboxMessage);
            _nextPublicationActivationRevision++;
            return Task.CompletedTask;
        }

        public Task ActivateExistingPublicationAsync(
            CatalogPublication targetPublication,
            Guid expectedCurrentPublicationId,
            CurrentPublicationPointer publicationPointer,
            CatalogPublicationActivationOutboxFactory outboxFactory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsurePointer(expectedCurrentPublicationId);
            Assert.Equal(targetPublication.Id, publicationPointer.PublicationId);
            var outboxMessage = outboxFactory(_nextPublicationActivationRevision);
            CurrentPublicationId = targetPublication.Id;
            OutboxMessages.Add(outboxMessage);
            _nextPublicationActivationRevision++;
            return Task.CompletedTask;
        }

        public Task AddClaimAsync(ListingClaim claim, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Claims are outside this publication flow test.");

        public Task<ListingClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Claims are outside this publication flow test.");

        public Task CompleteClaimVerificationAsync(
            ListingClaim claim,
            ListingAccessGrant grant,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Claims are outside this publication flow test.");

        public Task SaveClaimDecisionAsync(
            ListingClaim claim,
            CatalogOutboxMessage? outboxMessage,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Claims are outside this publication flow test.");

        private void EnsurePointer(Guid? expected)
        {
            if (CurrentPublicationId != expected)
            {
                throw new CatalogConflictException("Publication pointer mismatch.");
            }
        }
    }
}
