using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Promotion.Application;
using RabbitMQ.Client.Events;

namespace Aggregator.Promotion.Worker;

internal static class PromotionMessageEnvelopeValidation
{
    public static void ValidateEnvelope(
        BasicDeliverEventArgs eventArgs,
        string expectedRoutingKey,
        string expectedContractIdentity,
        string producerName)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        RequireText(expectedRoutingKey, nameof(expectedRoutingKey));
        RequireText(expectedContractIdentity, nameof(expectedContractIdentity));
        RequireText(producerName, nameof(producerName));
        if (!string.Equals(eventArgs.RoutingKey, expectedRoutingKey, StringComparison.Ordinal))
        {
            throw new JsonException(
                $"{producerName} event routing key '{eventArgs.RoutingKey}' is unsupported by Promotion.");
        }

        if (!string.Equals(
                eventArgs.BasicProperties.Type,
                expectedContractIdentity,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"{producerName} event contract '{eventArgs.BasicProperties.Type}' is unsupported by Promotion.");
        }

        if (!string.Equals(
                eventArgs.BasicProperties.ContentType,
                "application/json",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                eventArgs.BasicProperties.ContentEncoding,
                "utf-8",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new JsonException(
                $"{producerName} events must use application/json with utf-8 encoding.");
        }
    }

    public static void VerifyPayloadIntegrity(
        ReadOnlySpan<byte> payload,
        string expectedDigest,
        string producerName)
    {
        RequireText(producerName, nameof(producerName));
        if (expectedDigest.Length != 64 ||
            expectedDigest.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new JsonException($"{producerName} payload digest is invalid.");
        }

        var computedDigest = SHA256.HashData(payload);
        byte[] expectedBytes;
        try
        {
            expectedBytes = Convert.FromHexString(expectedDigest);
        }
        catch (FormatException exception)
        {
            throw new JsonException($"{producerName} payload digest is invalid.", exception);
        }

        if (!CryptographicOperations.FixedTimeEquals(computedDigest, expectedBytes))
        {
            throw new JsonException(
                $"{producerName} payload digest does not match the exact message bytes.");
        }
    }

    public static Guid ValidateMessageIdentity(
        Guid eventId,
        string? messageId,
        string producerName)
    {
        RequireText(producerName, nameof(producerName));
        if (eventId == Guid.Empty ||
            !Guid.TryParse(messageId, out var parsedMessageId) ||
            parsedMessageId == Guid.Empty ||
            parsedMessageId != eventId)
        {
            throw new JsonException(
                $"RabbitMQ message ID must match the {producerName} event ID.");
        }

        return parsedMessageId;
    }

    public static bool IsRetryable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is PromotionApplicationException { StatusCode: 503 } ||
               exception is DbException { IsTransient: true } ||
               exception is TimeoutException or IOException ||
               exception.InnerException is not null && IsRetryable(exception.InnerException);
    }

    public static Guid? ReadOptionalGuidHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null ||
            !headers.TryGetValue(name, out var rawValue) ||
            rawValue is null)
        {
            return null;
        }

        var value = ReadHeaderValue(rawValue, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Guid.TryParse(value, out var identifier) && identifier != Guid.Empty
            ? identifier
            : throw new JsonException(
                $"RabbitMQ header '{name}' must contain an absent value or a non-empty UUID.");
    }

    public static string ReadRequiredCorrelationId(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) ||
            correlationId.Length > 128 ||
            correlationId.Any(char.IsControl))
        {
            throw new JsonException(
                "RabbitMQ correlation ID is absent or invalid for the Promotion contract.");
        }

        return correlationId.Trim();
    }

    public static string ReadRequiredHeader(
        IDictionary<string, object?>? headers,
        string name)
    {
        if (headers is null ||
            !headers.TryGetValue(name, out var rawValue) ||
            rawValue is null)
        {
            throw new JsonException($"Required RabbitMQ header '{name}' is absent.");
        }

        var value = ReadHeaderValue(rawValue, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"RabbitMQ header '{name}' is empty.");
        }

        return value.Trim();
    }

    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }

    private static string ReadHeaderValue(object rawValue, string name) =>
        rawValue switch
        {
            byte[] bytes => Encoding.UTF8.GetString(bytes),
            ReadOnlyMemory<byte> memory => Encoding.UTF8.GetString(memory.Span),
            string text => text,
            _ => throw new JsonException(
                $"RabbitMQ header '{name}' has an unsupported value type."),
        };

    private static void RequireText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", parameterName);
        }
    }
}
