using System.Security.Cryptography;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Catalog.Application.Tests;

public sealed class CatalogEndToEndTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ConfigurationRevisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000001");
    private static readonly Guid SubjectId = Guid.Parse("0192f5f0-0000-7000-8000-000000000002");
    private static readonly Guid SubjectRevisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000003");
    private static readonly Guid ActorId = Guid.Parse("0192f5f0-0000-7000-8000-000000000004");
    private const string ConfigurationDigest = "190f3c410a3bcc2661ee325f6bbb4fa5646f9b215f91831deafdc5db50cdd08d";

    [Fact]
    public async Task CatalogFlowImportsConfigurationPublishesAndRollsBackExactRevisions()
    {
        var listingId = Guid.Parse("0192f5f0-0000-7000-8000-000000000010");
        var firstRevisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000011");
        var firstDecisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000012");
        var firstPublicationId = Guid.Parse("0192f5f0-0000-7000-8000-000000000013");
        var firstPublicationEventId = Guid.Parse("0192f5f0-0000-7000-8000-000000000014");
        var secondRevisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000015");
        var secondDecisionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000016");
        var secondPublicationId = Guid.Parse("0192f5f0-0000-7000-8000-000000000017");
        var secondPublicationEventId = Guid.Parse("0192f5f0-0000-7000-8000-000000000018");
        var rollbackEventId = Guid.Parse("0192f5f0-0000-7000-8000-000000000019");
        var repository = new InMemoryCatalogRepository();
        var artifactStore = new VerifyingArtifactStore();
        var idSource = new QueueCatalogIdSource(
            listingId,
            firstRevisionId,
            firstDecisionId,
            firstPublicationId,
            firstPublicationEventId,
            secondRevisionId,
            secondDecisionId,
            secondPublicationId,
            secondPublicationEventId,
            rollbackEventId);
        var timeProvider = new FixedTimeProvider(Timestamp);
        var actor = CatalogActor.Create(ActorId);
        var configurationService = new CatalogConfigurationService(repository, timeProvider);
        var listingService = new CatalogListingService(repository, idSource, timeProvider);
        var publicationService = new CatalogPublicationService(
            repository,
            artifactStore,
            idSource,
            timeProvider);

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
            CatalogKey.Create("berlin-recording-services"),
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
                new SubjectReferenceContract(
                    SubjectId,
                    SubjectRevisionId,
                    SubjectKindContract.Place)),
            actor,
            CancellationToken.None);
        Assert.Equal(listingId, listing.Id);
        Assert.Equal(1, listing.Version);

        var firstRevision = await listingService.AddRevisionAsync(
            listing.Id,
            new CreateListingRevisionRequest(
                ExpectedVersion: 1,
                ConfigurationRevisionId,
                listing.Subject,
                CreateContent(80m, Guid.Parse("0192f5f0-0000-7000-8000-000000000025"))),
            actor,
            CancellationToken.None);
        Assert.Equal(firstRevisionId, firstRevision.Id);

        var firstApproval = await listingService.ApproveAsync(
            listing.Id,
            new ApproveListingRevisionRequest(
                ExpectedVersion: 2,
                RevisionId: firstRevision.Id),
            actor,
            CancellationToken.None);
        Assert.Equal("approved", firstApproval.Decision);

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
        Assert.Equal(firstPublicationId, firstPublication.Id);
        Assert.True(firstPublication.IsCurrent);
        Assert.Equal(firstPublicationId, repository.CurrentPublicationId);
        Assert.Single(artifactStore.Artifacts);

        var secondRevision = await listingService.AddRevisionAsync(
            listing.Id,
            new CreateListingRevisionRequest(
                ExpectedVersion: 4,
                ConfigurationRevisionId,
                listing.Subject,
                CreateContent(95m, Guid.Parse("0192f5f0-0000-7000-8000-000000000026"))),
            actor,
            CancellationToken.None);
        Assert.Equal(secondRevisionId, secondRevision.Id);

        _ = await listingService.ApproveAsync(
            listing.Id,
            new ApproveListingRevisionRequest(
                ExpectedVersion: 5,
                RevisionId: secondRevision.Id),
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
        Assert.Equal(secondPublicationId, secondPublication.Id);
        Assert.Equal(secondPublicationId, repository.CurrentPublicationId);
        Assert.Equal(2, artifactStore.Artifacts.Count);

        var rollback = await publicationService.RollbackAsync(
            CatalogKey.Create("berlin-recording-services"),
            new RollbackPublicationRequest(
                TargetPublicationId: firstPublication.Id,
                ExpectedCurrentPublicationId: secondPublication.Id),
            actor,
            CancellationToken.None);
        Assert.Equal(firstPublication.Id, rollback.Id);
        Assert.Equal(firstPublication.Id, repository.CurrentPublicationId);
        Assert.Equal(3, repository.OutboxMessages.Count);
        Assert.Equal(
            [
                CatalogIntegrationEventTypes.PublicationActivated,
                CatalogIntegrationEventTypes.PublicationActivated,
                CatalogIntegrationEventTypes.PublicationActivated,
            ],
            repository.OutboxMessages.Select(message => message.EventType).ToArray());
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
                new CategoryDefinitionContract(
                    "recording-studio",
                    [SubjectKindContract.Place],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["de-DE"] = "Tonstudio",
                        ["en-GB"] = "Recording studio",
                    },
                    IsActive: true),
                new CategoryDefinitionContract(
                    "music-producer",
                    [SubjectKindContract.Provider],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["de-DE"] = "Musikproduzent",
                        ["en-GB"] = "Music producer",
                    },
                    IsActive: true),
            ],
            [
                new AttributeDefinitionContract(
                    "hourly-price",
                    AttributeValueKindContract.Decimal,
                    AttributeCardinalityContract.Single,
                    PublicFieldRequirementContract.Optional,
                    ["recording-studio"],
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["de-DE"] = "Stundenpreis",
                        ["en-GB"] = "Hourly price",
                    },
                    Minimum: null,
                    Maximum: null,
                    AllowedValues: [],
                    IsFilterable: true,
                    IsSortable: true),
            ]);

    private static ListingRevisionContentContract CreateContent(decimal hourlyPrice, Guid priceAssertionId)
    {
        var nameAssertionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000020");
        var categoryAssertionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000021");
        var geographyAssertionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000022");
        var contactAssertionId = Guid.Parse("0192f5f0-0000-7000-8000-000000000023");
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
                GeographyStateContract.BerlinCore,
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

    private sealed class QueueCatalogIdSource(params Guid[] values) : ICatalogIdSource
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

        public Task PutVerifiedAsync(
            string objectKey,
            ReadOnlyMemory<byte> content,
            string sha256Digest,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedDigest = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
            Assert.Equal(sha256Digest, observedDigest);
            Assert.True(Artifacts.TryAdd(objectKey, content.ToArray()));
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryCatalogRepository : ICatalogRepository
    {
        private readonly Dictionary<Guid, ProductConfiguration> _configurations = [];
        private readonly Dictionary<string, Guid> _activeConfigurations = new(StringComparer.Ordinal);
        private readonly Dictionary<Guid, Listing> _listings = [];
        private readonly Dictionary<Guid, ListingRevision> _revisions = [];
        private readonly Dictionary<Guid, CatalogPublication> _publications = [];
        private readonly Dictionary<Guid, ListingClaim> _claims = [];
        private long _nextPublicationSequence = 1;

        public Guid? CurrentPublicationId { get; private set; }

        public List<CatalogOutboxMessage> OutboxMessages { get; } = [];

        public Task AddConfigurationAsync(
            ProductConfiguration configuration,
            byte[] canonicalDocument,
            DateTimeOffset importedAtUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(configuration);
            Assert.NotEmpty(canonicalDocument);
            Assert.Equal(TimeSpan.Zero, importedAtUtc.Offset);
            Assert.True(_configurations.TryAdd(configuration.RevisionId, configuration));
            return Task.CompletedTask;
        }

        public Task<ProductConfiguration?> GetConfigurationAsync(
            Guid configurationRevisionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _configurations.TryGetValue(configurationRevisionId, out var configuration);
            return Task.FromResult(configuration);
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
            Assert.NotEqual(Guid.Empty, actorId);
            Assert.Equal(TimeSpan.Zero, activatedAtUtc.Offset);
            var actual = _activeConfigurations.TryGetValue(catalogKey.Value, out var current)
                ? current
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
            _listings.TryGetValue(listingId, out var listing);
            return Task.FromResult(listing);
        }

        public Task<ListingRevision?> GetListingRevisionAsync(
            Guid revisionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _revisions.TryGetValue(revisionId, out var revision);
            return Task.FromResult(revision);
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
            IReadOnlyList<PublicationSelectionState> result = selections
                .Select(selection => new PublicationSelectionState(
                    _listings[selection.ListingId],
                    _revisions[selection.ListingRevisionId]))
                .Where(selection => selection.Listing.CatalogKey == catalogKey)
                .ToArray();
            return Task.FromResult(result);
        }

        public Task<long> GetNextPublicationSequenceAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(catalogKey);
            return Task.FromResult(_nextPublicationSequence++);
        }

        public Task<Guid?> GetCurrentPublicationIdAsync(
            CatalogKey catalogKey,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(catalogKey);
            return Task.FromResult(CurrentPublicationId);
        }

        public Task<CatalogPublication?> GetPublicationAsync(
            Guid publicationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _publications.TryGetValue(publicationId, out var publication);
            return Task.FromResult(publication);
        }

        public Task CommitPublicationAsync(
            CatalogPublication publication,
            Guid? expectedCurrentPublicationId,
            IReadOnlyList<Listing> listings,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CurrentPublicationId != expectedCurrentPublicationId)
            {
                throw new CatalogConflictException("Publication pointer mismatch.");
            }

            Assert.True(_publications.TryAdd(publication.Id, publication));
            foreach (var listing in listings)
            {
                _listings[listing.Id] = listing;
            }

            CurrentPublicationId = publication.Id;
            OutboxMessages.Add(outboxMessage);
            return Task.CompletedTask;
        }

        public Task ActivateExistingPublicationAsync(
            CatalogPublication targetPublication,
            Guid expectedCurrentPublicationId,
            CurrentPublicationPointer publicationPointer,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CurrentPublicationId != expectedCurrentPublicationId)
            {
                throw new CatalogConflictException("Publication pointer mismatch.");
            }

            Assert.Equal(targetPublication.Id, publicationPointer.PublicationId);
            CurrentPublicationId = publicationPointer.PublicationId;
            OutboxMessages.Add(outboxMessage);
            return Task.CompletedTask;
        }

        public Task AddClaimAsync(ListingClaim claim, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(_claims.TryAdd(claim.Id, claim));
            return Task.CompletedTask;
        }

        public Task<ListingClaim?> GetClaimAsync(Guid claimId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _claims.TryGetValue(claimId, out var claim);
            return Task.FromResult(claim);
        }

        public Task CompleteClaimVerificationAsync(
            ListingClaim claim,
            ListingAccessGrant grant,
            CatalogOutboxMessage outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _claims[claim.Id] = claim;
            Assert.Equal(claim.Id, grant.ClaimId);
            OutboxMessages.Add(outboxMessage);
            return Task.CompletedTask;
        }

        public Task SaveClaimDecisionAsync(
            ListingClaim claim,
            CatalogOutboxMessage? outboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _claims[claim.Id] = claim;
            if (outboxMessage is not null)
            {
                OutboxMessages.Add(outboxMessage);
            }

            return Task.CompletedTask;
        }
    }
}
