using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Application.Tests;

public sealed class CatalogConfigurationProjectionSequencePolicyTests
{
    private static readonly Guid FirstConfigurationId =
        Guid.Parse("0198a710-0000-7000-8000-000000000001");
    private static readonly Guid SecondConfigurationId =
        Guid.Parse("0198a710-0000-7000-8000-000000000002");

    [Fact]
    public void FirstActivationMustStartAtRevisionOneWithoutPreviousPointer()
    {
        CatalogConfigurationProjectionSequencePolicy.RequireNext(
            current: null,
            CreateProjection(
                FirstConfigurationId,
                previousConfigurationId: null,
                aggregateRevision: 1));

        var exception = Assert.Throws<IngestionApplicationException>(() =>
            CatalogConfigurationProjectionSequencePolicy.RequireNext(
                current: null,
                CreateProjection(
                    SecondConfigurationId,
                    FirstConfigurationId,
                    aggregateRevision: 2)));
        Assert.Equal("INGESTION_CATALOG_CONFIGURATION_REVISION_GAP", exception.Code);
        Assert.Equal(503, exception.StatusCode);
    }

    [Fact]
    public void ExactNextRevisionAndPointerAreAccepted()
    {
        var current = new CatalogConfigurationProjectionCheckpoint(
            "berlin-recording",
            FirstConfigurationId,
            AggregateRevision: 1);

        CatalogConfigurationProjectionSequencePolicy.RequireNext(
            current,
            CreateProjection(
                SecondConfigurationId,
                FirstConfigurationId,
                aggregateRevision: 2));
    }

    [Fact]
    public void RevisionGapIsUnavailableAndRequiresReplay()
    {
        var current = new CatalogConfigurationProjectionCheckpoint(
            "berlin-recording",
            FirstConfigurationId,
            AggregateRevision: 1);

        var exception = Assert.Throws<IngestionApplicationException>(() =>
            CatalogConfigurationProjectionSequencePolicy.RequireNext(
                current,
                CreateProjection(
                    SecondConfigurationId,
                    FirstConfigurationId,
                    aggregateRevision: 3)));

        Assert.Equal("INGESTION_CATALOG_CONFIGURATION_REVISION_GAP", exception.Code);
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(2L, exception.Context["expectedAggregateRevision"]);
    }

    [Fact]
    public void NewMessageCannotReuseAnAppliedRevision()
    {
        var current = new CatalogConfigurationProjectionCheckpoint(
            "berlin-recording",
            FirstConfigurationId,
            AggregateRevision: 2);

        var exception = Assert.Throws<IngestionApplicationException>(() =>
            CatalogConfigurationProjectionSequencePolicy.RequireNext(
                current,
                CreateProjection(
                    SecondConfigurationId,
                    FirstConfigurationId,
                    aggregateRevision: 2)));

        Assert.Equal("INGESTION_CATALOG_CONFIGURATION_REVISION_REUSED", exception.Code);
        Assert.Equal(409, exception.StatusCode);
    }

    [Fact]
    public void NextRevisionMustContinueTheExactConfigurationPointer()
    {
        var current = new CatalogConfigurationProjectionCheckpoint(
            "berlin-recording",
            FirstConfigurationId,
            AggregateRevision: 1);
        var unrelatedConfigurationId =
            Guid.Parse("0198a710-0000-7000-8000-000000000003");

        var exception = Assert.Throws<IngestionApplicationException>(() =>
            CatalogConfigurationProjectionSequencePolicy.RequireNext(
                current,
                CreateProjection(
                    SecondConfigurationId,
                    unrelatedConfigurationId,
                    aggregateRevision: 2)));

        Assert.Equal(
            "INGESTION_CATALOG_CONFIGURATION_POINTER_CHAIN_MISMATCH",
            exception.Code);
    }

    private static CatalogConfigurationProjection CreateProjection(
        Guid configurationId,
        Guid? previousConfigurationId,
        long aggregateRevision) =>
        new(
            "berlin-recording",
            "berlin-recording-services",
            configurationId,
            previousConfigurationId,
            new string('a', 64),
            "berlin-core-and-nearby",
            [IngestionEntityKindContract.Place, IngestionEntityKindContract.Provider],
            aggregateRevision,
            Guid.Parse($"0198a710-0000-7000-8000-{aggregateRevision + 100:D12}"),
            new string('b', 64),
            new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero),
            new string('c', 64));
}
