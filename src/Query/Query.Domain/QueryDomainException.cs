namespace Aggregator.Query.Domain;

public sealed class QueryDomainException : Exception
{
    public QueryDomainException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

internal static class QueryContractRules
{
    public static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new QueryDomainException("QUERY_IDENTIFIER_REQUIRED", $"'{parameterName}' must be a non-empty UUID.");
        }

        return value;
    }

    public static string RequireText(string value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new QueryDomainException("QUERY_TEXT_REQUIRED", $"'{parameterName}' must be non-empty.");
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new QueryDomainException("QUERY_TEXT_TOO_LONG", $"'{parameterName}' exceeds {maximumLength} characters.");
        }

        return normalized;
    }

    public static string RequireKey(string value, string parameterName) =>
        RequireText(value, parameterName, 200);

    public static string RequireDigest(string value, string parameterName)
    {
        var normalized = RequireText(value, parameterName, 64);
        if (normalized.Length != 64 || normalized.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new QueryDomainException("QUERY_DIGEST_INVALID", $"'{parameterName}' must be a lowercase SHA-256 digest.");
        }

        return normalized;
    }

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new QueryDomainException("QUERY_TIMESTAMP_NOT_UTC", $"'{parameterName}' must be normalized to UTC.");
        }

        return value;
    }
}
