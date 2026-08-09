using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Aggregator.Analytics.Application;

namespace Aggregator.Analytics.Worker;

/// <summary>Technical validation shared by Analytics RabbitMQ consumers without owning producer semantics.</summary>
internal static class AnalyticsMessageEnvelopeValidation
{
    public static void VerifyPayloadIntegrity(
        ReadOnlySpan<byte> payload,
        string expectedDigest,
        string contractName)
    {
        var normalizedContractName = RequireContractName(contractName);
        if (payload.IsEmpty)
        {
            throw new JsonException($"{normalizedContractName} payload is empty.");
        }

        if (string.IsNullOrWhiteSpace(expectedDigest) ||
            expectedDigest.Length != 64 ||
            expectedDigest.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
        {
            throw new JsonException(
                $"{normalizedContractName} payload digest must be canonical lowercase SHA-256.");
        }

        var actualDigest = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualDigest),
                Convert.FromHexString(expectedDigest)))
        {
            throw new JsonException(
                $"{normalizedContractName} payload digest does not match the received UTF-8 bytes.");
        }
    }

    public static void ValidateMessageIdentity(
        Guid eventId,
        string? messageId,
        string contractName)
    {
        var normalizedContractName = RequireContractName(contractName);
        if (eventId == Guid.Empty ||
            !Guid.TryParse(messageId, out var parsedMessageId) ||
            parsedMessageId != eventId)
        {
            throw new JsonException(
                $"{normalizedContractName} RabbitMQ message ID must match the producer event ID.");
        }
    }

    public static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is AnalyticsCommandException { StatusCode: 503 } ||
               exception is DbException { IsTransient: true } ||
               exception is TimeoutException or IOException ||
               exception.InnerException is not null && IsRetryable(exception.InnerException);
    }

    private static string RequireContractName(string contractName)
    {
        if (string.IsNullOrWhiteSpace(contractName) || contractName.Length > 120)
        {
            throw new ArgumentException(
                "Message contract name must contain between one and 120 characters.",
                nameof(contractName));
        }

        return contractName.Trim();
    }
}
