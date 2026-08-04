using System.Security.Cryptography;
using System.Text;

namespace Aggregator.Analytics.Api;

public sealed record AnalyticsApiOptions
{
    public required string InternalMetricsKey { get; init; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(InternalMetricsKey) || InternalMetricsKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Analytics internal metrics key must contain at least 32 characters.");
        }
    }

    public bool IsInternalKeyValid(string? suppliedKey)
    {
        if (string.IsNullOrWhiteSpace(suppliedKey))
        {
            return false;
        }

        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(InternalMetricsKey));
        var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
