using Aggregator.Promotion.Worker;

namespace Promotion.Worker.Tests;

public sealed class PromotionWorkerOptionsTests
{
    private static readonly Guid SystemActorId =
        Guid.Parse("0198b400-0000-7000-8000-000000000001");

    [Fact]
    public void ValidOptionsProducePromotionOwnedTransportConfiguration()
    {
        var options = PromotionWorkerOptions.Create(
            "Host=localhost;Database=promotion_db;Username=promotion_app;Password=test",
            new Uri("amqp://guest:guest@localhost:5672/"),
            "aggregator.events",
            "promotion-worker-test",
            SystemActorId);

        var outbox = options.CreateOutboxOptions();
        var publisher = options.CreatePublisherOptions();

        Assert.Equal("messaging", outbox.Schema);
        Assert.Equal(8, outbox.MaximumDeliveryAttempts);
        Assert.Equal("promotion-worker-test", outbox.DispatcherIdentity);
        Assert.Equal("aggregator.events", publisher.Exchange);
    }

    [Fact]
    public void EmptySystemActorFailsBeforeHostBuild()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PromotionWorkerOptions.Create(
                "Host=localhost;Database=promotion_db;Username=promotion_app;Password=test",
                new Uri("amqp://guest:guest@localhost:5672/"),
                "aggregator.events",
                "promotion-worker-test",
                Guid.Empty));

        Assert.Equal("systemActorId", exception.ParamName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(501)]
    public void InvalidTransitionBatchIsRejected(int batchSize)
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            PromotionWorkerOptions.Create(
                "Host=localhost;Database=promotion_db;Username=promotion_app;Password=test",
                new Uri("amqp://guest:guest@localhost:5672/"),
                "aggregator.events",
                "promotion-worker-test",
                SystemActorId,
                transitionBatchSize: batchSize));

        Assert.Equal("TransitionBatchSize", exception.ParamName);
    }

    [Fact]
    public void NonRabbitBrokerUriIsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            PromotionWorkerOptions.Create(
                "Host=localhost;Database=promotion_db;Username=promotion_app;Password=test",
                new Uri("https://broker.test"),
                "aggregator.events",
                "promotion-worker-test",
                SystemActorId));

        Assert.Equal("BrokerUri", exception.ParamName);
    }
}
