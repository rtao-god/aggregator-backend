using System.Globalization;

namespace Aggregator.Query.Domain;

public sealed class QueryLocalePolicy
{
    private QueryLocalePolicy(string defaultLocale, IReadOnlyList<string> supportedLocales)
    {
        DefaultLocale = defaultLocale;
        SupportedLocales = supportedLocales;
    }

    public string DefaultLocale { get; }

    public IReadOnlyList<string> SupportedLocales { get; }

    public static QueryLocalePolicy Create(string defaultLocale, IEnumerable<string> supportedLocales)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultLocale);
        ArgumentNullException.ThrowIfNull(supportedLocales);

        var normalizedDefaultLocale = NormalizeLocale(defaultLocale, nameof(defaultLocale));
        var normalizedSupportedLocales = supportedLocales
            .Select(locale => NormalizeLocale(locale, nameof(supportedLocales)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (normalizedSupportedLocales.Length == 0)
        {
            throw new QueryDomainException(
                "QUERY_SUPPORTED_LOCALE_REQUIRED",
                "A Query projection must declare at least one supported locale.");
        }

        if (!normalizedSupportedLocales.Contains(normalizedDefaultLocale, StringComparer.OrdinalIgnoreCase))
        {
            throw new QueryDomainException(
                "QUERY_DEFAULT_LOCALE_UNSUPPORTED",
                $"Default locale '{normalizedDefaultLocale}' is not part of the supported locale set.");
        }

        return new QueryLocalePolicy(
            normalizedDefaultLocale,
            Array.AsReadOnly(normalizedSupportedLocales));
    }

    public bool Supports(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            return false;
        }

        try
        {
            var normalized = CultureInfo.GetCultureInfo(locale.Trim()).Name;
            return SupportedLocales.Contains(normalized, StringComparer.OrdinalIgnoreCase);
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static string NormalizeLocale(string locale, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(locale))
        {
            throw new QueryDomainException(
                "QUERY_LOCALE_REQUIRED",
                $"'{parameterName}' contains an empty locale.");
        }

        try
        {
            return CultureInfo.GetCultureInfo(locale.Trim()).Name;
        }
        catch (CultureNotFoundException exception)
        {
            throw new QueryDomainException(
                "QUERY_LOCALE_INVALID",
                $"Locale '{locale}' is not recognized: {exception.Message}");
        }
    }
}
