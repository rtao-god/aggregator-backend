using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Application.Tests;

public sealed class CatalogConfigurationProjectionServiceTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid EventId =
        Guid.Parse("0198a700-0000-7000-8000-000000000001");
    private static readonly Guid ConfigurationRevisionId =
        Guid.Parse("0198a700-0000-7000-8000-000000000002");
    private const string ConfigurationDigest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PayloadDigest =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public async Task ValidProducerActivationCreatesExactProjectionAndInbox()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogConfigurationActivationService(
            store,
            new FixedTimeProvider(Timestamp.AddSeconds(1)));

        var result = await service.ApplyAsync(
            CreateActivation(),
            PayloadDigest,
            "corr-catalog-config-0001",
            CancellationToken.None);

        Assert.Equal(CatalogConfigurationProjectionDisposition.Applied, result.Disposition);
        var projection = Assert.IsType<CatalogConfigurationProjection>(store.Projection);
        Assert.Equal("berlin-recording", projection.SiteKey);
        Assert.Equal("berlin-recording-services", projection.CatalogKey);
        Assert.Equal(ConfigurationRevisionId, projection.ConfigurationRevisionId);
        Assert.Null(projection.PreviousConfigurationRevisionId);
        Assert.Equal(ConfigurationDigest, projection.ConfigurationDigest);
        Assert.Equal("berlin-core-and-nearby", projection.MarketAreaKey);
        Assert.Equal(
            [IngestionEntityKindContract.Place, IngestionEntityKindContract.Provider],
            projection.SupportedListingKinds);
        Assert.Equal(1, projection.AggregateRevision);
        Assert.Equal(EventId, projection.SourceEventId);
        Assert.Equal(PayloadDigest, projection.SourcePayloadDigest);
        Assert.Equal(Timestamp, projection.ActivatedAtUtc);
        Assert.Matches("^[0-9a-f]{64}$", projection.ProjectionDigest);

        var inbox = Assert.IsType<CatalogConfigurationInboxMessage>(store.InboxMessage);
        Assert.Equal(EventId, inbox.EventId);
        Assert.Equal(CatalogIntegrationEventTypes.ConfigurationActivated, inbox.RoutingKey);
        Assert.Equal(CatalogIntegrationEventContracts.ConfigurationActivated, inbox.ContractIdentity);
        Assert.Equal(PayloadDigest, inbox.PayloadDigest);
        Assert.Equal("corr-catalog-config-0001", inbox.CorrelationId);
        Assert.Equal(Timestamp.AddSeconds(1), inbox.ReceivedAtUtc);
    }

    [Fact]
    public async Task NonCanonicalListingKindOrderIsRejectedBeforePersistence()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogConfigurationActivationService(
            store,
            new FixedTimeProvider(Timestamp.AddSeconds(1)));
        var activation = CreateActivation() with
        {
            SupportedListingKinds =
            [
                SubjectKindContract.Provider,
                SubjectKindContract.Place,
            ],
        };

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.ApplyAsync(
                activation,
                PayloadDigest,
                "corr-catalog-config-0002",
                CancellationToken.None));

        Assert.Equal(
            "INGESTION_CATALOG_CONFIGURATION_LISTING_KINDS_NOT_CANONICAL",
            exception.Code);
        Assert.Null(store.Projection);
    }

    [Fact]
    public async Task ActivationRevisionRequiresExactPreviousPointerShape()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogConfigurationActivationService(
            store,
            new FixedTimeProvider(Timestamp.AddSeconds(1)));
        var activation = CreateActivation() with
        {
            AggregateRevision = 2,
            PreviousConfigurationRevisionId = null,
        };

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.ApplyAsync(
                activation,
                PayloadDigest,
                "corr-catalog-config-0003",
                CancellationToken.None));

        Assert.Equal("INGESTION_CATALOG_CONFIGURATION_REVISION_INVALID", exception.Code);
        Assert.Null(store.Projection);
    }

    [Fact]
    public async Task NumericOrOrganizationListingKindIsRejected()
    {
        var store = new CapturingStore();
        var service = new ApplyCatalogConfigurationActivationService(
            store,
            new FixedTimeProvider(Timestamp.AddSeconds(1)));
        var activation = CreateActivation() with
        {
            SupportedListingKinds = [SubjectKindContract.Organization],
        };

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.ApplyAsync(
                activation,
                PayloadDigest,
                "corr-catalog-config-0004",
                CancellationToken.None));

        Assert.Equal(
            "INGESTION_CATALOG_CONFIGURATION_LISTING_KIND_UNSUPPORTED",
            exception.Code);
        Assert.Null(store.Projection);
    }

    private static CatalogConfigurationActivated CreateActivation() =>
        new(
            EventId,
            "berlin-recording",
            "berlin-recording-services",
            ConfigurationRevisionId,
            PreviousConfigurationRevisionId: null,
            ConfigurationDigest,
            "berlin-core-and-nearby",
            [SubjectKindContract.Place, SubjectKindContract.Provider],
            AggregateRevision: 1,
            Timestamp);

    private sealed class FixedTimeProvider(DateTimeOffset timestamp) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => timestamp;
    }

    private sealed class CapturingStore : ICatalogConfigurationProjectionStore
    {
        public CatalogConfigurationProjection? Projection { get; private set; }

        public CatalogConfigurationInboxMessage? InboxMessage { get; private set; }

        public Task<CatalogConfigurationProjectionResult> ApplyAsync(
            CatalogConfigurationProjection projection,
            CatalogConfigurationInboxMessage inboxMessage,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Projection = projection;
            InboxMessage = inboxMessage;
            return Task.FromResult(
                new CatalogConfigurationProjectionResult(
                    projection,
                    CatalogConfigurationProjectionDisposition.Applied));
        }
    }
}
