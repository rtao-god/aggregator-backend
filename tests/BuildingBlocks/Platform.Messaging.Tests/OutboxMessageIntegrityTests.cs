using System.Security.Cryptography;
using System.Text;
using Platform.Messaging;

namespace Platform.Messaging.Tests;

public sealed class OutboxMessageIntegrityTests
{
    [Fact]
    public void ExactCanonicalPayloadIsAccepted()
    {
        const string payload = "{\"eventId\":\"0192f5f0-0000-7000-8000-000000000001\"}";
        var message = CreateMessage(payload, ComputeDigest(payload));

        var bytes = OutboxMessageIntegrity.GetVerifiedPayloadBytes(message);

        Assert.Equal(payload, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void MutatedPayloadIsRejectedBeforeBrokerAccess()
    {
        const string originalPayload = "{\"state\":\"approved\"}";
        var message = CreateMessage(
            "{\"state\":\"rejected\"}",
            ComputeDigest(originalPayload));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OutboxMessageIntegrity.GetVerifiedPayloadBytes(message));

        Assert.Contains("digest does not match", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NonCanonicalDigestRepresentationIsRejected()
    {
        var message = CreateMessage("{}", new string('A', 64));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OutboxMessageIntegrity.GetVerifiedPayloadBytes(message));

        Assert.Contains("invalid SHA-256 digest", exception.Message, StringComparison.Ordinal);
    }

    private static OutboxMessage CreateMessage(string payload, string digest) =>
        new(
            Guid.Parse("0192f5f0-0000-7000-8000-000000000001"),
            "catalog.publication.activated",
            "aggregator.catalog.publication-activated@1",
            payload,
            digest,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            "corr.messaging-test:0001",
            CausationId: null);

    private static string ComputeDigest(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
}
