namespace Aggregator.Ingestion.Domain;

public sealed class IngestionDomainException : Exception
{
    public IngestionDomainException(string code, string message)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A non-empty Ingestion domain error code is required.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}

internal static class IngestionContractRules
{
    public static string RequireText(string value, string parameterName, int maximumLength = 500)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new IngestionDomainException("INGESTION_TEXT_REQUIRED", $"'{parameterName}' must be non-empty.");
        }

        if (value.Length > maximumLength)
        {
            throw new IngestionDomainException(
                "INGESTION_TEXT_TOO_LONG",
                $"'{parameterName}' exceeds {maximumLength} characters.");
        }

        return value;
    }

    public static string RequireProductKey(string value, string parameterName)
    {
        RequireText(value, parameterName, 96);
        if (!char.IsAsciiLetter(value[0]) || char.IsUpper(value[0]))
        {
            throw new IngestionDomainException(
                "INGESTION_KEY_INVALID",
                $"'{parameterName}' must begin with a lowercase ASCII letter.");
        }

        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '-') || char.IsUpper(character))
            {
                throw new IngestionDomainException(
                    "INGESTION_KEY_INVALID",
                    $"'{parameterName}' must contain only lowercase ASCII letters, digits, and hyphens.");
            }
        }

        return value;
    }

    public static string RequireSemanticKey(string value, string parameterName)
    {
        RequireText(value, parameterName, 200);
        if (value.Any(char.IsControl))
        {
            throw new IngestionDomainException(
                "INGESTION_SEMANTIC_KEY_INVALID",
                $"'{parameterName}' must not contain control characters.");
        }

        return value;
    }

    public static string RequireDigest(string value, string parameterName)
    {
        RequireText(value, parameterName, 64);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new IngestionDomainException(
                "INGESTION_DIGEST_INVALID",
                $"'{parameterName}' must be a lowercase SHA-256 hexadecimal digest.");
        }

        return value;
    }

    public static Guid RequireId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new IngestionDomainException(
                "INGESTION_IDENTIFIER_REQUIRED",
                $"'{parameterName}' must be a non-empty UUID.");
        }

        return value;
    }

    public static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new IngestionDomainException(
                "INGESTION_TIMESTAMP_NOT_UTC",
                $"'{parameterName}' must be normalized to UTC.");
        }

        return value;
    }
}

public readonly record struct ImportBatchId(Guid Value)
{
    public static ImportBatchId Create(Guid value) => new(IngestionContractRules.RequireId(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}

public readonly record struct IngestionActorId(Guid Value)
{
    public static IngestionActorId Create(Guid value) => new(IngestionContractRules.RequireId(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
