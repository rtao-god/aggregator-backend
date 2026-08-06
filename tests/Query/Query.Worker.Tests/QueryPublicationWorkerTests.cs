using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Promotion.Contracts;
using Aggregator.Query.Application;
using Aggregator.Query.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Query.Worker.Tests;

public sealed class QueryPublicationWorkerTests
{
    [Fact]
    public void ValidPublicationWorkerOptionsAreAccepted()
    {
        var options = CreatePublicationOptions();

        options.Validate();

        Assert.Equal((ushort)16, options.PrefetchCount);
        Assert.Equal("catalog.publication.activated", options.RoutingKey);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)257)]
    public void UnsafePublicationPrefetchIsRejected(ushort prefetchCount)
    {
        var options = CreatePublicationOptions() with
        {
            PrefetchCount = prefetchCount,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("https://rabbitmq.example")]
    [InlineData("file:///tmp/rabbitmq")]
    public void NonAmqpPublicationBrokerIsRejected(string brokerUri)
    {
        var options = CreatePublicationOptions() with
        {
            BrokerUri = new Uri(brokerUri),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void ActiveCompositionRegistersCurrentPublicationPromotionAndVisibilityWorkers()
    {
        var services = new ServiceCollection();

        services.AddQueryWorker(
            CreatePublicationOptions(),
            CreatePromotionOptions(),
            CreateVisibilityOptions());

        var hostedWorkerTypes = services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .Where(type => type is not null)
            .ToArray();
        Assert.Equal(
            [
                typeof(CatalogPublicationProjectionWorker),
                typeof(PromotionOverlayProjectionWorker),
                typeof(VisibilitySafetyProjectionWorker),
            ],
            hostedWorkerTypes);
        Assert.Equal(PromotionIntegrationEventTypes.PlacementChanged, CreatePromotionOptions().RoutingKey);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void UnsafePromotionDeliveryLimitIsRejected(int deliveryLimit)
    {
        var options = CreatePromotionOptions() with
        {
            DeliveryLimit = deliveryLimit,
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(60001)]
    public void UnsafePromotionRetryDelayIsRejected(int retryDelayMilliseconds)
    {
        var options = CreatePromotionOptions() with
        {
            RetryDelay = TimeSpan.FromMilliseconds(retryDelayMilliseconds),
        };

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void PromotionPayloadDigestMustMatchExactBody()
    {
        var payload = Encoding.UTF8.GetBytes("""{"eventId":"0198a500-0000-7000-8000-000000000001"}""");
        var digest = Convert
            .ToHexString(SHA256.HashData(payload))
            .ToLowerInvariant();

        PromotionOverlayProjectionWorker.VerifyPayloadIntegrity(payload, digest);

        Assert.Throws<JsonException>(() =>
            PromotionOverlayProjectionWorker.VerifyPayloadIntegrity(
                payload,
                new string('0', 64)));
    }

    [Fact]
    public void PromotionMessageIdentityMustMatchEventIdentity()
    {
        var eventId = Guid.Parse("0198a500-0000-7000-8000-000000000001");

        PromotionOverlayProjectionWorker.ValidateMessageIdentity(
            eventId,
            eventId.ToString("D"));

        Assert.Throws<JsonException>(() =>
            PromotionOverlayProjectionWorker.ValidateMessageIdentity(
                eventId,
                Guid.Parse("0198a500-0000-7000-8000-000000000002").ToString("D")));
    }

    [Fact]
    public void OnlyUnavailableOrTransientProjectionFailuresAreRequeued()
    {
        var unavailable = new QueryProjectionException(
            "Query.PromotionProjection",
            "QUERY_PUBLIC_READ_UNAVAILABLE",
            503,
            "Projection is unavailable.",
            "Replay after the base projection is active.");
        var invalid = new QueryProjectionException(
            "Query.PromotionProjection",
            "QUERY_PROMOTION_LISTING_NOT_IN_BASE",
            422,
            "Listing is absent.",
            "End the placement.");

        Assert.True(PromotionOverlayProjectionWorker.IsRetryableProjectionFailure(unavailable));
        Assert.True(PromotionOverlayProjectionWorker.IsRetryableProjectionFailure(new TimeoutException()));
        Assert.False(PromotionOverlayProjectionWorker.IsRetryableProjectionFailure(invalid));
        Assert.False(PromotionOverlayProjectionWorker.IsRetryableProjectionFailure(new JsonException()));
    }

    private static QueryWorkerOptions CreatePublicationOptions() =>
        new()
        {
            BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
            Exchange = "aggregator.events",
            RoutingKey = "catalog.publication.activated",
            Queue = "query.catalog-publication-projection",
            PrefetchCount = 16,
        };

    private static QueryPromotionWorkerOptions CreatePromotionOptions() =>
        new()
        {
            BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
            Exchange = "aggregator.events",
            Queue = "query.promotion-placement-projection",
            DeadLetterExchange = "aggregator.dead-letter",
            DeadLetterQueue = "query.promotion-placement-projection.dead-letter",
            RoutingKey = PromotionIntegrationEventTypes.PlacementChanged,
            PrefetchCount = 8,
            DeliveryLimit = 8,
            RetryDelay = TimeSpan.FromMilliseconds(500),
        };

    private static QueryVisibilityWorkerOptions CreateVisibilityOptions() =>
        new()
        {
            BrokerUri = new Uri("amqp://guest:guest@localhost:5672/"),
            Exchange = "aggregator.events",
            Queue = "query.catalog-visibility-safety",
            DeadLetterExchange = "aggregator.dead-letter",
            DeadLetterQueue = "query.catalog-visibility-safety.dead-letter",
            PrefetchCount = 4,
            DeliveryLimit = 8,
            RetryDelay = TimeSpan.FromMilliseconds(500),
        };
}
