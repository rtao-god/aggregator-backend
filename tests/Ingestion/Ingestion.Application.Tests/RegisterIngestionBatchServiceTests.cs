using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Aggregator.Ingestion.Domain;

namespace Ingestion.Application.Tests;

public sealed class RegisterIngestionBatchServiceTests
{
    private static readonly DateTimeOffset RegisteredAt =
        new(2026, 8, 4, 5, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactProducerAndCatalogRevisionRegisterBatch()
    {
        var fixture = CreateManifestFixture();
        var batchId = Guid.Parse("0198a123-0000-7000-8000-000000000100");
        var repository = new CapturingRepository();
        var service = new RegisterIngestionBatchService(
            new FixedProducerRegistry(active: true),
            new FixedCatalogReferenceReader(fixture.Manifest.TargetCatalogConfigurationRevisionId),
            repository,
            new FixedClock(RegisteredAt),
            new FixedIdSource(batchId));

        var result = await service.RegisterAsync(
            new RegisterIngestionBatchCommand(
                fixture.Manifest,
                fixture.ManifestDigest,
                "register-export-42",
                "data-collection-platform"),
            CancellationToken.None);

        Assert.False(result.Replayed);
        Assert.Equal(batchId, result.Batch.Id.Value);
        Assert.Equal(fixture.Manifest.TargetCatalogKey, result.Batch.TargetCatalogKey);
        Assert.Equal(fixture.Manifest.PayloadDigest, result.Batch.PayloadDigest);
        Assert.Equal("data-collection-platform", repository.CallerServiceIdentity);
        Assert.NotNull(repository.CommandIdentity);
        Assert.Equal(
            "ingestion.batch.register:berlin-recording-services",
            repository.CommandIdentity.Scope);
    }

    [Fact]
    public async Task StaleTargetConfigurationRevisionFailsBeforePersistence()
    {
        var fixture = CreateManifestFixture();
        var repository = new CapturingRepository();
        var service = new RegisterIngestionBatchService(
            new FixedProducerRegistry(active: true),
            new FixedCatalogReferenceReader(Guid.CreateVersion7()),
            repository,
            new FixedClock(RegisteredAt),
            new FixedIdSource(Guid.CreateVersion7()));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.RegisterAsync(
                new RegisterIngestionBatchCommand(
                    fixture.Manifest,
                    fixture.ManifestDigest,
                    "register-export-42",
                    "data-collection-platform"),
                CancellationToken.None));

        Assert.Equal("INGESTION_TARGET_CONFIGURATION_REVISION_MISMATCH", exception.Code);
        Assert.Null(repository.RegisteredBatch);
    }

    [Fact]
    public async Task InactiveProducerFailsBeforeCatalogLookupAndPersistence()
    {
        var fixture = CreateManifestFixture();
        var catalogReader = new FixedCatalogReferenceReader(
            fixture.Manifest.TargetCatalogConfigurationRevisionId);
        var repository = new CapturingRepository();
        var service = new RegisterIngestionBatchService(
            new FixedProducerRegistry(active: false),
            catalogReader,
            repository,
            new FixedClock(RegisteredAt),
            new FixedIdSource(Guid.CreateVersion7()));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.RegisterAsync(
                new RegisterIngestionBatchCommand(
                    fixture.Manifest,
                    fixture.ManifestDigest,
                    "register-export-42",
                    "data-collection-platform"),
                CancellationToken.None));

        Assert.Equal("INGESTION_PRODUCER_NOT_ALLOWED", exception.Code);
        Assert.Equal(0, catalogReader.ReadCount);
        Assert.Null(repository.RegisteredBatch);
    }

    private static ManifestFixture CreateManifestFixture()
    {
        var manifest = new AggregatorCandidateIngestionManifest(
            AggregatorCandidateIngestionContract.Identity,
            AggregatorCandidateIngestionContract.Revision,
            "collector-berlin",
            "build-2026-08-04",
            Guid.Parse("0198a123-0000-7000-8000-000000000101"),
            new string('a', 64),
            "berlin-recording",
            "berlin-recording-services",
            Guid.Parse("0198a123-0000-7000-8000-000000000102"),
            RegisteredAt.AddMinutes(-5),
            1,
            new string('b', 64),
            new string('c', 64),
            [
                new IngestionSourcePolicyReferenceContract(
                    "official-website",
                    new string('d', 64),
                    CandidateUsagePolicyContract.Publishable),
            ],
            [
                new IngestionPackageArtifactContract(
                    IngestionArtifactRoleContract.CandidatePayload,
                    "ingestion/quarantine/package.json",
                    new string('e', 64),
                    4_096,
                    "application/json"),
            ]);
        return new ManifestFixture(
            manifest,
            IngestionPackageValidator.ComputeManifestDigest(manifest));
    }

    private sealed record ManifestFixture(
        AggregatorCandidateIngestionManifest Manifest,
        string ManifestDigest);

    private sealed class FixedProducerRegistry(bool active) : IIngestionProducerRegistry
    {
        public Task<RegisteredIngestionProducer?> GetAsync(
            string producerIdentity,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<RegisteredIngestionProducer?>(
                new RegisteredIngestionProducer(
                    producerIdentity,
                    active,
                    [AggregatorCandidateIngestionContract.Revision]));
        }
    }

    private sealed class FixedCatalogReferenceReader(Guid configurationRevisionId) :
        ICatalogIngestionReferenceReader
    {
        public int ReadCount { get; private set; }

        public Task<CatalogIngestionReference?> GetAsync(
            string siteKey,
            string catalogKey,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult<CatalogIngestionReference?>(
                new CatalogIngestionReference(
                    siteKey,
                    catalogKey,
                    configurationRevisionId,
                    [IngestionEntityKindContract.Place, IngestionEntityKindContract.Provider],
                    7));
        }
    }

    private sealed class CapturingRepository : IIngestionBatchRepository
    {
        public ImportBatch? RegisteredBatch { get; private set; }

        public IngestionCommandIdentity? CommandIdentity { get; private set; }

        public string? CallerServiceIdentity { get; private set; }

        public Task<IngestionBatchRegistrationResult> RegisterAsync(
            ImportBatch batch,
            AggregatorCandidateIngestionManifest manifest,
            IngestionCommandIdentity commandIdentity,
            string callerServiceIdentity,
            CancellationToken cancellationToken)
        {
            RegisteredBatch = batch;
            CommandIdentity = commandIdentity;
            CallerServiceIdentity = callerServiceIdentity;
            return Task.FromResult(
                new IngestionBatchRegistrationResult(
                    IngestionBatchSnapshot.From(batch),
                    false));
        }

        public Task<IngestionBatchSnapshot?> ReadAsync(
            ImportBatchId batchId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IngestionBatchSnapshot?>(null);
    }

    private sealed class FixedClock(DateTimeOffset value) : IIngestionClock
    {
        public DateTimeOffset GetUtcNow() => value;
    }

    private sealed class FixedIdSource(Guid value) : IIngestionIdSource
    {
        public Guid CreateId() => value;
    }
}
