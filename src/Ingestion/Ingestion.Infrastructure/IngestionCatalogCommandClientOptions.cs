using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Fail-fast configuration for the authenticated Ingestion-to-Catalog command boundary.</summary>
public sealed class IngestionCatalogCommandClientOptions
{
    public const string SectionName = "Ingestion:CatalogCommand";
    internal const string CommandClientName = "ingestion-catalog-command";
    internal const string TokenClientName = "ingestion-catalog-token";

    public required Uri BaseAddress { get; init; }

    public required Uri TokenEndpoint { get; init; }

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    public required string Scope { get; init; }

    public required TimeSpan RequestTimeout { get; init; }

    public required TimeSpan RefreshSkew { get; init; }

    public required bool AllowInsecureHttp { get; init; }

    public static IngestionCatalogCommandClientOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetRequiredSection(SectionName);
        var options = new IngestionCatalogCommandClientOptions
        {
            BaseAddress = ReadAbsoluteUri(section, nameof(BaseAddress), ensureTrailingSlash: true),
            TokenEndpoint = ReadAbsoluteUri(section, nameof(TokenEndpoint), ensureTrailingSlash: false),
            ClientId = ReadRequired(section, nameof(ClientId)),
            ClientSecret = ReadRequired(section, nameof(ClientSecret)),
            Scope = ReadRequired(section, nameof(Scope)),
            RequestTimeout = ReadTimeSpan(section, nameof(RequestTimeout)),
            RefreshSkew = ReadTimeSpan(section, nameof(RefreshSkew)),
            AllowInsecureHttp = ReadBoolean(section, nameof(AllowInsecureHttp)),
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        ValidateEndpoint(BaseAddress, nameof(BaseAddress));
        ValidateEndpoint(TokenEndpoint, nameof(TokenEndpoint));
        if (!AllowInsecureHttp &&
            (BaseAddress.Scheme != Uri.UriSchemeHttps || TokenEndpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw InvalidConfiguration(
                "Catalog and token endpoints must use HTTPS unless AllowInsecureHttp is explicitly enabled for a local environment.");
        }

        ValidateSecret(ClientId, nameof(ClientId), 500);
        ValidateSecret(ClientSecret, nameof(ClientSecret), 2_000);
        ValidateSecret(Scope, nameof(Scope), 500);
        if (RequestTimeout < TimeSpan.FromSeconds(1) || RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw InvalidConfiguration("RequestTimeout must be between one second and two minutes.");
        }

        if (RefreshSkew < TimeSpan.FromSeconds(5) || RefreshSkew > TimeSpan.FromMinutes(5))
        {
            throw InvalidConfiguration("RefreshSkew must be between five seconds and five minutes.");
        }
    }

    private static void ValidateEndpoint(Uri endpoint, string name)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri ||
            endpoint.Scheme is not (Uri.UriSchemeHttps or Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw InvalidConfiguration($"{name} must be an absolute HTTP(S) URI without a fragment.");
        }
    }

    private static void ValidateSecret(string value, string name, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw InvalidConfiguration($"{name} is missing or outside its bounded text contract.");
        }
    }

    private static Uri ReadAbsoluteUri(
        IConfigurationSection section,
        string key,
        bool ensureTrailingSlash)
    {
        var value = ReadRequired(section, key);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw InvalidConfiguration($"{section.Path}:{key} must be an absolute URI.");
        }

        if (!ensureTrailingSlash || uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
        {
            return uri;
        }

        return new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
    }

    private static TimeSpan ReadTimeSpan(IConfigurationSection section, string key)
    {
        var value = ReadRequired(section, key);
        if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var result))
        {
            throw InvalidConfiguration($"{section.Path}:{key} must be a TimeSpan.");
        }

        return result;
    }

    private static bool ReadBoolean(IConfigurationSection section, string key)
    {
        var value = ReadRequired(section, key);
        if (!bool.TryParse(value, out var result))
        {
            throw InvalidConfiguration($"{section.Path}:{key} must be true or false.");
        }

        return result;
    }

    private static string ReadRequired(IConfigurationSection section, string key)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidConfiguration($"{section.Path}:{key} is required.");
        }

        return value.Trim();
    }

    private static InvalidOperationException InvalidConfiguration(string message) =>
        new($"Invalid {SectionName} configuration: {message}");
}
