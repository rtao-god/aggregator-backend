namespace Aggregator.Analytics.Domain;

public sealed class AnalyticsDomainException : Exception
{
    public AnalyticsDomainException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

internal static class AnalyticsDomainRules
{
    public static void RequireIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_IDENTIFIER_REQUIRED",
                $"'{parameterName}' must be a non-empty UUID.");
        }
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_TIMESTAMP_NOT_UTC",
                $"'{parameterName}' must be normalized to UTC.");
        }
    }

    public static string RequireKey(string value, string parameterName, int maximumLength = 100)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_KEY_INVALID",
                $"'{parameterName}' must contain between 1 and {maximumLength} characters.");
        }

        var normalized = value.Trim();
        if (!char.IsAsciiLetter(normalized[0]) || char.IsUpper(normalized[0]) ||
            normalized.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_') ||
                char.IsUpper(character)))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_KEY_INVALID",
                $"'{parameterName}' must contain lowercase ASCII letters, digits, hyphens, or underscores.");
        }

        return normalized;
    }

    public static string RequireDigest(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_DIGEST_REQUIRED",
                $"'{parameterName}' is required.");
        }

        var normalized = value.Trim();
        if (normalized.Length != 64 ||
            normalized.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_DIGEST_INVALID",
                $"'{parameterName}' must be a lowercase SHA-256 hexadecimal digest.");
        }

        return normalized;
    }
}
