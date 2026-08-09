using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Ingestion.Api;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;

namespace Ingestion.Api.Tests;

public sealed class IngestionDeliveryApiTests
{
    private static readonly JsonSerializerOptions ClientJsonOptions = CreateClientJsonOptions();
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ExactDeliveryLedgerIsReturnedFromReadOnlyOwnerPath()
    {
        using var factory = new IngestionApiFactory();
        var batchId = Guid.Parse("0198a800-0000-7000-8000-000000000001");
        var deliveryId = Guid.Parse("0198a800-0000-7000-8000-000000000002");
        var listingId = Guid.Parse("0198a800-0000-7000-8000-000000000003");
        var listingRevisionId = Guid.Parse("0198a800-0000-7000-8000-000000000004");
        factory.Backend.SetCatalogDeliveries(new IngestionCatalogDeliveryCollection(
            batchId,
            [IngestionCatalogDeliverySnapshot.Create(
                deliveryId,
                batchId,
                "item-001",
                "aggregator.catalog.ingestion.upsert-draft@1",
                new string('a', 64),
                IngestionCatalogDeliveryState.Succeeded,
                1,
                leaseExpiresAtUtc: null,
                nextAttemptAtUtc: null,
                listingId,
                listingRevisionId,
                failureCode: null,
                failureDetail: null,
                CreatedAtUtc,
                CreatedAtUtc.AddSeconds(2))]));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/ingestion/batches/{batchId:D}/deliveries");
        AddReadAuthorization(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<IngestionCatalogDeliveriesResponse>(
            ClientJsonOptions);
        Assert.NotNull(result);
        Assert.Equal(batchId, result.BatchId);
        var delivery = Assert.Single(result.Deliveries);
        Assert.Equal(deliveryId, delivery.DeliveryId);
        Assert.Equal(IngestionCatalogDeliveryStateContract.Succeeded, delivery.State);
        Assert.Equal(listingId, delivery.CatalogListingId);
        Assert.Equal(listingRevisionId, delivery.CatalogListingRevisionId);
        Assert.Null(delivery.LeaseExpiresAtUtc);
        Assert.Null(delivery.FailureCode);
        Assert.Null(delivery.FailureDetail);
    }

    [Fact]
    public async Task MissingDeliveryBatchReturnsTypedNotFoundInsteadOfEmptySuccess()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        var batchId = Guid.Parse("0198a800-0000-7000-8000-000000000099");
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/ingestion/batches/{batchId:D}/deliveries");
        AddReadAuthorization(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(
            "INGESTION_BATCH_NOT_FOUND",
            payload.RootElement.GetProperty("code").GetString());
        Assert.Equal(
            "Ingestion.Batches",
            payload.RootElement.GetProperty("owner").GetString());
    }

    [Fact]
    public async Task AnonymousDeliveryReadIsRejected()
    {
        using var factory = new IngestionApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/ingestion/batches/{Guid.CreateVersion7():D}/deliveries");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static void AddReadAuthorization(HttpRequestMessage request)
    {
        request.Headers.Add(IngestionApiFactory.AuthenticationHeader, "authenticated");
        request.Headers.Add(IngestionApiFactory.SubjectHeader, "collector-service");
        request.Headers.Add(
            IngestionApiFactory.ScopesHeader,
            IngestionAuthorizationPolicies.Read);
    }

    private static JsonSerializerOptions CreateClientJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}
