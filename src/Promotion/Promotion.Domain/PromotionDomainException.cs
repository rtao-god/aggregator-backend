using System.Globalization;
using System.Text.RegularExpressions;

namespace Aggregator.Promotion.Domain;

public sealed class PromotionDomainException : InvalidOperationException
{
    public PromotionDomainException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code.Trim();
    }

    public string Code { get; }
}

internal static partial class PromotionDomainRules
{
    public static Guid RequireIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty identifier is required.", parameterName);
        }

        return value;
    }

    public static string RequireKey(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.Length > 120 || !KeyPattern().IsMatch(normalized))
        {
            throw new ArgumentException(
                "A key must contain lowercase letters, digits and single hyphen separators.",
                parameterName);
        }

        return normalized;
    }

    public static string RequireText(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().Normalize();
        if (normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"Text must contain at most {maximumLength} non-control characters.",
                parameterName);
        }

        return normalized;
    }

    public static string RequireLocale(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        try
        {
            return CultureInfo.GetCultureInfo(value.Trim()).Name;
        }
        catch (CultureNotFoundException exception)
        {
            throw new ArgumentException($"'{value}' is not a supported locale identifier.", parameterName, exception);
        }
    }

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A UTC timestamp is required.", parameterName);
        }

        return value;
    }

    public static string RequireDigest(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (value.Length != 64 ||
            value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 digest is required.", parameterName);
        }

        return value;
    }

    public static void RequireExpectedRevision(long actualRevision, long expectedRevision, string owner)
    {
        if (expectedRevision <= 0)
        {
            throw new PromotionDomainException(
                "PROMOTION_EXPECTED_REVISION_INVALID",
                $"{owner} expected revision must be positive.");
        }

        if (actualRevision != expectedRevision)
        {
            throw new PromotionDomainException(
                "PROMOTION_REVISION_CONFLICT",
                $"{owner} expected revision '{expectedRevision}' but is at '{actualRevision}'.");
        }
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
