using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Query.Application;
using Aggregator.Query.Worker;

namespace Query.Worker.Tests;

public sealed class VisibilitySafetyProjectionWorkerTests
{
    [Fact]
    public void PayloadIntegrityAcceptsExactBodyDigest()
    {
        var payload = Encoding.UTF8.GetBytes("{\"eventId\":\"0198fe00-0000-7000-8000-000000000001\"}");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        VisibilitySafetyProjectionWorker.VerifyPayloadIntegrity(payload, digest);
    }

    [Fact]
    public void PayloadIntegrityRejectsDivergentBody()
    {
        var payload = Encoding.UTF8.GetBytes("{\"state\":\"active\"}");

        Assert.Throws<JsonException>(() =>
            VisibilitySafetyProjectionWorker.VerifyPayloadIntegrity(
                payload,
                new string('0', 64)));
    }

    [Fact]
    public void MessageIdentityMustEqualProducerEventId()
    {
        var eventId = Guid.Parse("0198fe00-0000-7000-8000-000000000002");

        VisibilitySafetyProjectionWorker.ValidateMessageIdentity(
            eventId,
            eventId.ToString("D"));
        Assert.Throws<JsonException>(() =>
            VisibilitySafetyProjectionWorker.ValidateMessageIdentity(
                eventId,
                Guid.Parse("0198fe00-0000-7000-8000-000000000003").ToString("D")));
    }

    [Fact]
    public void OnlyUnavailableProjectionFailureIsRetryable()
    {
        var unavailable = new QueryProjectionException(
            "Query.VisibilitySafety",
            "QUERY_VISIBILITY_PUBLIC_READ_MISSING",
            503,
            "Public read is unavailable.",
            "Activate a publication.");
        var invalid = new QueryProjectionException(
            "Query.VisibilitySafety",
            "QUERY_VISIBILITY_TARGET_INVALID",
            422,
            "Target is invalid.",
            "Correct the producer event.");

        Assert.True(VisibilitySafetyProjectionWorker.IsRetryableProjectionFailure(unavailable));
        Assert.False(VisibilitySafetyProjectionWorker.IsRetryableProjectionFailure(invalid));
    }

    [Fact]
    public void WorkerOptionsRequireAmqpBrokerAndBoundedDelivery()
    {
        var valid = new QueryVisibilityWorkerOptions
        {
            BrokerUri = new Uri("amqp://guest:guest@rabbitmq:5672/"),
        };
        valid.Validate();

        var invalid = valid with { BrokerUri = new Uri("https://rabbitmq.example") };
        Assert.Throws<InvalidOperationException>(invalid.Validate);
    }
}
