using System.Security.Cryptography;
using System.Text;

namespace Platform.Messaging;

/// <summary>Verifies the producer-owned digest against the exact UTF-8 outbox payload.</summary>
public static class OutboxMessageIntegrity
{
    /// <summary>Returns exact payload bytes only after canonical SHA-256 verification succeeds.</summary>
    public static byte[] GetVerifiedPayloadBytes(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.PayloadJson);
        if (!IsCanonicalSha256Digest(message.PayloadDigest))
        {
            throw new InvalidOperationException(
                $"Outbox message '{message.MessageId}' has an invalid SHA-256 digest representation.");
        }

        var payloadBytes = Encoding.UTF8.GetBytes(message.PayloadJson);
        var expectedDigest = Convert.FromHexString(message.PayloadDigest);
        var actualDigest = SHA256.HashData(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedDigest, actualDigest))
        {
            throw new InvalidOperationException(
                $"Outbox message '{message.MessageId}' payload digest does not match its exact UTF-8 payload.");
        }

        return payloadBytes;
    }

    private static bool IsCanonicalSha256Digest(string? value) =>
        value is { Length: 64 } &&
        value.All(static character =>
  character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
