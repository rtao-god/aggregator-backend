using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Query.Application;
using Aggregator.Query.Worker;

namespace Query.Worker.Tests;

public sealed class CatalogPublicationProjectionWorkerContractTests
{
    [Fact]
    public void PayloadIntegrityAcceptsOnlyExactBodyDigest()
    {
        var payload = Encoding.UTF8.GetBytes("{\"eventId\":\"01990000-0000-7000-8000-000000000001\"}");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        CatalogPublicationProjectionWorker.VerifyPayloadIntegrity(payload, digest);
        Assert.Throws<JsonException>(() =>
            CatalogPublicationProjectionWorker.VerifyPayloadIntegrity(
                payload,
                new string('0', 64)));
    }

    [Fact]
    public void MessageIdentityMustEqualProducerEventId()
    {
        var eventId = Guid.Parse("01990000-0000-7000-8000-000000000002");

        CatalogPublicationProjectionWorker.ValidateMessageIdentity(
            eventId,
            eventId.ToString("D"));
        Assert.Throws<JsonException>(() =>
            CatalogPublicationProjectionWorker.ValidateMessageIdentity(
                eventId,
                Guid.Parse("01990000-0000-7000-8000-000000000003").ToString("D")));
    }

    [Fact]
    public void OnlyUnavailableOrTransientProjectionFailuresAreRetryable()
    {
        var unavailable = new QueryProjectionException(
            "Query.PublicationRecomposition",
            "QUERY_PUBLICATION_RECOMPOSITION_PENDING",
            503,
            "Recomposition is pending.",
            "Retry later.");
        var invalid = new QueryProjectionException(
            "Query.PublicationRecomposition",
            "QUERY_PUBLICATION_EVENT_ID_REUSED",
            409,
            "Event identity conflicts.",
            "Repair the producer event.");

        Assert.True(CatalogPublicationProjectionWorker.IsRetryableProjectionFailure(unavailable));
        Assert.True(CatalogPublicationProjectionWorker.IsRetryableProjectionFailure(new TimeoutException()));
        Assert.False(CatalogPublicationProjectionWorker.IsRetryableProjectionFailure(invalid));
        Assert.False(CatalogPublicationProjectionWorker.IsRetryableProjectionFailure(new JsonException()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void UnsafeDeliveryLimitIsRejected(int deliveryLimit)
    {
        var options = CreateOptions() with { DeliveryLimit = deliveryLimit };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    private static QueryWorkerOptions CreateOptions() =>
        new()
        {
            BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
        };
}
