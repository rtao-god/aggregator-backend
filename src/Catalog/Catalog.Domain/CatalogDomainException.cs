using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Aggregator.Catalog.Domain;

public sealed class CatalogDomainException : Exception
{
    public CatalogDomainException(string code, string message)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("A non-empty catalog domain error code is required.", nameof(code));
        }

        Code = code;
    }

    public string Code { get; }
}

internal static class CatalogTextRules
{
    public static void RequireText(
        [NotNull] string? value,
        string parameterName,
        int maximumLength = 500)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CatalogDomainException("CATALOG_TEXT_REQUIRED", $"'{parameterName}' must be non-empty.");
        }

        if (value.Length > maximumLength)
        {
            throw new CatalogDomainException("CATALOG_TEXT_TOO_LONG", $"'{parameterName}' exceeds {maximumLength} characters.");
        }
    }

    public static void RequireKey([NotNull] string? value, string parameterName)
    {
        RequireText(value, parameterName, 100);
        if (!char.IsAsciiLetter(value[0]) || char.IsUpper(value[0]))
        {
            throw new CatalogDomainException("CATALOG_KEY_INVALID", $"'{parameterName}' must begin with a lowercase ASCII letter.");
        }

        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character == '-') || char.IsUpper(character))
            {
                throw new CatalogDomainException(
                    "CATALOG_KEY_INVALID",
                    $"'{parameterName}' must contain only lowercase ASCII letters, digits, and hyphens.");
            }
        }
    }

    public static void RequireLocale([NotNull] string? value, string parameterName)
    {
        RequireText(value, parameterName, 35);
        try
        {
            _ = CultureInfo.GetCultureInfo(value);
        }
        catch (CultureNotFoundException exception)
        {
            throw new CatalogDomainException("CATALOG_LOCALE_INVALID", $"'{parameterName}' is not a recognized locale: {exception.Message}");
        }
    }

    public static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new CatalogDomainException("CATALOG_TIMESTAMP_NOT_UTC", $"'{parameterName}' must be normalized to UTC.");
        }
    }

    public static void RequireDigest([NotNull] string? value, string parameterName)
    {
        RequireText(value, parameterName, 64);
        if (value.Length != 64 || value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new CatalogDomainException("CATALOG_DIGEST_INVALID", $"'{parameterName}' must be a lowercase SHA-256 hex digest.");
        }
    }

    public static void RequireIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new CatalogDomainException("CATALOG_IDENTIFIER_REQUIRED", $"'{parameterName}' must be a non-empty UUID.");
        }
    }
}
