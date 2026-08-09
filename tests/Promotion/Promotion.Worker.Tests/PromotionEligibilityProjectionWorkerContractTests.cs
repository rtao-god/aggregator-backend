using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Worker;

namespace Promotion.Worker.Tests;

public sealed class PromotionEligibilityProjectionWorkerContractTests
{
    [Fact]
    public void ExactPayloadDigestIsAccepted()
    {
        var payload = Encoding.UTF8.GetBytes("{\"eventId\":\"0198ff00-0000-7000-8000-000000000001\"}");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        PromotionEligibilityProjectionWorker.VerifyPayloadIntegrity(payload, digest);
    }

    [Fact]
    public void PayloadDigestMismatchIsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("{\"value\":1}");

        _ = Assert.Throws<JsonException>(() =>
            PromotionEligibilityProjectionWorker.VerifyPayloadIntegrity(
                payload,
                new string('a', 64)));
    }

    [Fact]
    public void BrokerMessageIdentityMustMatchProducerEvent()
    {
        var eventId = Guid.Parse("0198ff00-0000-7000-8000-000000000002");

        Assert.Equal(
            eventId,
            PromotionEligibilityProjectionWorker.ValidateMessageIdentity(
                eventId,
                eventId.ToString("D")));
        _ = Assert.Throws<JsonException>(() =>
            PromotionEligibilityProjectionWorker.ValidateMessageIdentity(
                eventId,
                Guid.Parse("0198ff00-0000-7000-8000-000000000003").ToString("D")));
    }

    [Fact]
    public void RootCatalogEventMayHaveNoCausationIdentity()
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["causation-id"] = null,
        };

        Assert.Null(PromotionEligibilityProjectionWorker.ReadOptionalGuidHeader(
            headers,
            "causation-id"));
    }

    [Fact]
    public void PresentCausationIdentityMustBeNonEmptyUuid()
    {
        var causationId = Guid.Parse("0198ff00-0000-7000-8000-000000000004");
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["causation-id"] = Encoding.UTF8.GetBytes(causationId.ToString("D")),
        };

        Assert.Equal(
            causationId,
            PromotionEligibilityProjectionWorker.ReadOptionalGuidHeader(
                headers,
                "causation-id"));
    }

    [Fact]
    public void OnlyUnavailableOrTransientFailuresAreRetryable()
    {
        var gap = new PromotionApplicationException(
            "Promotion.EligibilityProjection",
            "PROMOTION_ELIGIBILITY_REVISION_GAP",
            503,
            "Missing Catalog eligibility revision.",
            "Replay the exact missing revision.");
        var divergence = new PromotionApplicationException(
            "Promotion.EligibilityProjection",
            "PROMOTION_ELIGIBILITY_REVISION_DIVERGED",
            409,
            "Catalog eligibility revision diverged.",
            "Inspect the producer outbox.");

        Assert.True(PromotionEligibilityProjectionWorker.IsRetryable(gap));
        Assert.False(PromotionEligibilityProjectionWorker.IsRetryable(divergence));
        Assert.True(PromotionEligibilityProjectionWorker.IsRetryable(new TimeoutException()));
    }
}
