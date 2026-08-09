using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Application.Tests;

public sealed class IngestionCatalogDeliveryReadServiceTests
{
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 9, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid BatchId =
        Guid.Parse("0198a700-0000-7000-8000-000000000001");
    private static readonly Guid DeliveryId =
        Guid.Parse("0198a700-0000-7000-8000-000000000002");
    private static readonly Guid ListingId =
        Guid.Parse("0198a700-0000-7000-8000-000000000003");
    private static readonly Guid ListingRevisionId =
        Guid.Parse("0198a700-0000-7000-8000-000000000004");

    [Fact]
    public async Task ReadMapsExactSucceededDeliveryLedger()
    {
        var snapshot = IngestionCatalogDeliverySnapshot.Create(
            DeliveryId,
            BatchId,
            "item-001",
            "aggregator.catalog.ingestion.upsert-draft@1",
            new string('a', 64),
            IngestionCatalogDeliveryState.Succeeded,
            1,
            leaseExpiresAtUtc: null,
            nextAttemptAtUtc: null,
            ListingId,
            ListingRevisionId,
            failureCode: null,
            failureDetail: null,
            CreatedAtUtc,
            CreatedAtUtc.AddSeconds(2));
        var service = new ReadIngestionCatalogDeliveriesService(
            new StubReader(new IngestionCatalogDeliveryCollection(BatchId, [snapshot])));

        var response = await service.ReadAsync(BatchId, CancellationToken.None);

        Assert.Equal(BatchId, response.BatchId);
        var delivery = Assert.Single(response.Deliveries);
        Assert.Equal(DeliveryId, delivery.DeliveryId);
        Assert.Equal(IngestionCatalogDeliveryStateContract.Succeeded, delivery.State);
        Assert.Equal(ListingId, delivery.CatalogListingId);
        Assert.Equal(ListingRevisionId, delivery.CatalogListingRevisionId);
        Assert.Null(delivery.FailureCode);
        Assert.Null(delivery.FailureDetail);
    }

    [Fact]
    public async Task MissingBatchReturnsTypedOwnerFailure()
    {
        var service = new ReadIngestionCatalogDeliveriesService(new StubReader(result: null));

        var exception = await Assert.ThrowsAsync<IngestionApplicationException>(() =>
            service.ReadAsync(BatchId, CancellationToken.None));

        Assert.Equal("Ingestion.Batches", exception.Owner);
        Assert.Equal("INGESTION_BATCH_NOT_FOUND", exception.Code);
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public void SuccessfulStateRejectsAbsentCatalogRevisionIdentity()
    {
        var exception = Assert.Throws<IngestionApplicationException>(() =>
            IngestionCatalogDeliverySnapshot.Create(
                DeliveryId,
                BatchId,
                "item-001",
                "aggregator.catalog.ingestion.upsert-draft@1",
                new string('b', 64),
                IngestionCatalogDeliveryState.Succeeded,
                1,
                leaseExpiresAtUtc: null,
                nextAttemptAtUtc: null,
                ListingId,
                catalogListingRevisionId: null,
                failureCode: null,
                failureDetail: null,
                CreatedAtUtc,
                CreatedAtUtc.AddSeconds(2)));

        Assert.Equal("INGESTION_DELIVERY_SUCCESS_STATE_INVALID", exception.Code);
    }

    [Fact]
    public void RetriedPendingStateRequiresScheduleAndFailureTuple()
    {
        var exception = Assert.Throws<IngestionApplicationException>(() =>
            IngestionCatalogDeliverySnapshot.Create(
                DeliveryId,
                BatchId,
                "item-001",
                "aggregator.catalog.ingestion.upsert-draft@1",
                new string('c', 64),
                IngestionCatalogDeliveryState.Pending,
                1,
                leaseExpiresAtUtc: null,
                nextAttemptAtUtc: null,
                catalogListingId: null,
                catalogListingRevisionId: null,
                failureCode: null,
                failureDetail: null,
                CreatedAtUtc,
                CreatedAtUtc.AddSeconds(2)));

        Assert.Equal("INGESTION_DELIVERY_RETRY_STATE_INVALID", exception.Code);
    }

    private sealed class StubReader(IngestionCatalogDeliveryCollection? result)
        : IIngestionCatalogDeliveryReader
    {
        public Task<IngestionCatalogDeliveryCollection?> ReadAsync(
            Guid batchId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }
}
