using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Worker;

namespace Analytics.Worker.Tests;

public sealed class AnalyticsPublicReadProjectionWorkerContractTests
{
    [Fact]
    public void ExactPayloadDigestIsAccepted()
    {
        var payload = Encoding.UTF8.GetBytes("{\"eventId\":\"0198a600-0000-7000-8000-000000000001\"}");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        AnalyticsPublicReadProjectionWorker.VerifyPayloadIntegrity(payload, digest);
    }

    [Fact]
    public void PayloadDigestMismatchIsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("{\"value\":1}");

        _ = Assert.Throws<JsonException>(() =>
            AnalyticsPublicReadProjectionWorker.VerifyPayloadIntegrity(
                payload,
                new string('a', 64)));
    }

    [Fact]
    public void BrokerMessageIdentityMustMatchProducerEvent()
    {
        var eventId = Guid.Parse("0198a600-0000-7000-8000-000000000002");

        AnalyticsPublicReadProjectionWorker.ValidateMessageIdentity(
            eventId,
            eventId.ToString("D"));
        _ = Assert.Throws<JsonException>(() =>
            AnalyticsPublicReadProjectionWorker.ValidateMessageIdentity(
                eventId,
                Guid.Parse("0198a600-0000-7000-8000-000000000003").ToString("D")));
    }

    [Fact]
    public void OnlyUnavailableOrTransientFailuresAreRetryable()
    {
        var unavailable = new AnalyticsCommandException(
            "Analytics.PublicReference",
            "ANALYTICS_PUBLIC_ACTIVATION_REVISION_GAP",
            503,
            "Missing activation revision.",
            "Replay the next expected revision.");
        var invalid = new AnalyticsCommandException(
            "Analytics.PublicReference",
            "ANALYTICS_PUBLIC_MEMBERSHIP_DIGEST_MISMATCH",
            422,
            "Invalid producer membership.",
            "Correct the producer event.");

        Assert.True(AnalyticsPublicReadProjectionWorker.IsRetryable(unavailable));
        Assert.False(AnalyticsPublicReadProjectionWorker.IsRetryable(invalid));
        Assert.True(AnalyticsPublicReadProjectionWorker.IsRetryable(new TimeoutException()));
    }
}
