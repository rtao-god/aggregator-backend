using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Worker;

namespace Ingestion.Worker.Tests;

public sealed class IngestionCatalogConfigurationProjectionWorkerContractTests
{
    [Fact]
    public void ExactPayloadDigestIsAccepted()
    {
        var payload = Encoding.UTF8.GetBytes(
            "{\"eventId\":\"0198a700-0000-7000-8000-000000000001\"}");
        var digest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        IngestionCatalogConfigurationProjectionWorker.VerifyPayloadIntegrity(payload, digest);
    }

    [Fact]
    public void PayloadDigestMismatchIsRejected()
    {
        var payload = Encoding.UTF8.GetBytes("{\"value\":1}");

        _ = Assert.Throws<JsonException>(() =>
            IngestionCatalogConfigurationProjectionWorker.VerifyPayloadIntegrity(
                payload,
                new string('a', 64)));
    }

    [Fact]
    public void BrokerMessageIdentityMustMatchProducerEvent()
    {
        var eventId = Guid.Parse("0198a700-0000-7000-8000-000000000002");

        IngestionCatalogConfigurationProjectionWorker.ValidateMessageIdentity(
            eventId,
            eventId.ToString("D"));
        _ = Assert.Throws<JsonException>(() =>
            IngestionCatalogConfigurationProjectionWorker.ValidateMessageIdentity(
                eventId,
                Guid.Parse("0198a700-0000-7000-8000-000000000003").ToString("D")));
    }

    [Fact]
    public void OnlyUnavailableOrTransientFailuresAreRetryable()
    {
        var unavailable = new IngestionApplicationException(
            "Ingestion.CatalogProjection",
            "INGESTION_CATALOG_CONFIGURATION_REVISION_GAP",
            503,
            "Missing activation revision.",
            "Replay the next expected revision.");
        var invalid = new IngestionApplicationException(
            "Ingestion.CatalogProjection",
            "INGESTION_CATALOG_CONFIGURATION_INBOX_CORRUPT",
            409,
            "Message identity was reused.",
            "Quarantine the divergent message.");

        Assert.True(IngestionCatalogConfigurationProjectionWorker.IsRetryable(unavailable));
        Assert.False(IngestionCatalogConfigurationProjectionWorker.IsRetryable(invalid));
        Assert.True(IngestionCatalogConfigurationProjectionWorker.IsRetryable(new TimeoutException()));
    }
}
