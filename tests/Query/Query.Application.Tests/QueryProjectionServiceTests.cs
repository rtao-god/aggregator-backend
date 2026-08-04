using Aggregator.Catalog.Contracts;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;

namespace Query.Application.Tests;

public sealed class QueryProjectionServiceTests
{
    [Fact]
    public async Task PublicationEventBuildsAndActivatesExactCompositeReadRevision()
    {
        var timestamp = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var publicationId = Guid.Parse("0198a200-0000-7000-8000-000000000001");
        var configId = Guid.Parse("0198a200-0000-7000-8000-000000000002");
        var eventId = Guid.Parse("0198a200-0000-7000-8000-000000000003");
        var artifact = CreateArtifact(publicationId, configId, timestamp);
        var reader = new StubArtifactReader(artifact);
        var store = new RecordingProjectionStore();
        var service = new QueryProjectionService(
            reader,
            store,
            new FixedClock(timestamp.AddMinutes(1)),
            new FixedIdFactory(
                Guid.Parse("0198a200-0000-7000-8000-000000000010"),
                Guid.Parse("0198a200-0000-7000-8000-000000000011"),
                Guid.Parse("0198a200-0000-7000-8000-000000000012"),
                Guid.Parse("0198a200-0000-7000-8000-000000000013")));
        var activation = new CatalogPublicationActivated(
            eventId,
            publicationId,
            "berlin-recording-services",
            configId,
            1,
            "catalog/publications/sealed/example.json",
            new string('a', 64),
            PublicationActivationKindContract.Publication,
            null,
            timestamp);

        var result = await service.ApplyPublicationAsync(activation, new string('b', 64), CancellationToken.None);

        Assert.False(result.Replayed);
        Assert.NotNull(store.Activation);
        Assert.Equal(publicationId, result.PublicReadRevision.SourcePublicationId);
        Assert.Single(store.Activation.BaseProjection.Documents);
        Assert.Equal(eventId, store.InboxMessage?.EventId);
    }

    [Fact]
    public async Task ArtifactIdentityMismatchFailsBeforeProjectionPersistence()
    {
        var timestamp = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        var artifact = CreateArtifact(
            Guid.Parse("0198a200-0000-7000-8000-000000000020"),
            Guid.Parse("0198a200-0000-7000-8000-000000000021"),
            timestamp);
        var store = new RecordingProjectionStore();
        var service = new QueryProjectionService(
            new StubArtifactReader(artifact),
            store,
            new FixedClock(timestamp),
            new FixedIdFactory(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()));
        var activation = new CatalogPublicationActivated(
            Guid.CreateVersion7(),
            Guid.Parse("0198a200-0000-7000-8000-000000000099"),
            artifact.CatalogKey,
            artifact.ConfigurationRevisionId,
            artifact.PublicationSequence,
            "catalog/publications/sealed/mismatch.json",
            new string('a', 64),
            PublicationActivationKindContract.Publication,
            null,
            timestamp);

        var exception = await Assert.ThrowsAsync<QueryProjectionException>(() => service.ApplyPublicationAsync(
            activation,
            new string('b', 64),
            CancellationToken.None));

        Assert.Equal("QUERY_PUBLICATION_IDENTITY_MISMATCH", exception.Code);
        Assert.Null(store.Activation);
    }

    private static CatalogPublicationArtifact CreateArtifact(
        Guid publicationId,
        Guid configurationId,
        DateTimeOffset timestamp) =>
        new(
            CatalogPublicationArtifactContract.Identity,
            CatalogPublicationArtifactContract.Revision,
            publicationId,
            "berlin-recording-services",
            configurationId,
            1,
            timestamp,
            [new PublicListingDocument(
                Guid.Parse("0198a200-0000-7000-8000-000000000030"),
                Guid.Parse("0198a200-0000-7000-8000-000000000031"),
                Guid.Parse("0198a200-0000-7000-8000-000000000032"),
                Guid.Parse("0198a200-0000-7000-8000-000000000033"),
                SubjectKindContract.Place,
                [new PublicLocalizedText("de-DE", FieldValueStateContract.Observed, "Studio Beispiel", null, Guid.CreateVersion7())],
                [new PublicLocalizedText("de-DE", FieldValueStateContract.Observed, "Aufnahmestudio", null, Guid.CreateVersion7())],
                ["recording-studio"],
                [],
                new PublicGeography(GeographyStateContract.BerlinCore, 52.5m, 13.4m, "mitte", Guid.CreateVersion7()),
                [],
                [],
                [],
                new string('c', 64))]);

    private sealed class StubArtifactReader(CatalogPublicationArtifact artifact) : ICatalogPublicationArtifactReader
    {
        public Task<CatalogPublicationArtifact> ReadAsync(
            string objectKey,
            string expectedDigest,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(objectKey);
            ArgumentException.ThrowIfNullOrWhiteSpace(expectedDigest);
            return Task.FromResult(artifact);
        }
    }

    private sealed class RecordingProjectionStore : IQueryProjectionStore
    {
        public QueryProjectionActivation? Activation { get; private set; }

        public QueryInboxMessage? InboxMessage { get; private set; }

        public Task<QueryProjectionActivationResult> ActivateAsync(
            QueryProjectionActivation activation,
            QueryInboxMessage inboxMessage,
            CancellationToken cancellationToken)
        {
            Activation = activation;
            InboxMessage = inboxMessage;
            return Task.FromResult(new QueryProjectionActivationResult(activation.PublicReadRevision, false));
        }
    }

    private sealed class FixedClock(DateTimeOffset value) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FixedIdFactory(params Guid[] values) : IQueryIdFactory
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid Create() => _values.Dequeue();
    }
}
