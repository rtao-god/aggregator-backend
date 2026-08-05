namespace Aggregator.Acceptance.Runner;

public sealed record AcceptanceOptions
{
    public required Uri IdentityBaseUrl { get; init; }

    public required Uri CollectorBaseUrl { get; init; }

    public required Uri CatalogControlBaseUrl { get; init; }

    public required Uri AnalyticsControlBaseUrl { get; init; }

    public required Uri QueryBaseUrl { get; init; }

    public required Uri AnalyticsBaseUrl { get; init; }

    public required Uri PromotionOverlayBaseUrl { get; init; }

    public required string AcceptanceKey { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    public void Validate()
    {
        ValidateHttpUri(IdentityBaseUrl, nameof(IdentityBaseUrl));
        ValidateHttpUri(CollectorBaseUrl, nameof(CollectorBaseUrl));
        ValidateHttpUri(CatalogControlBaseUrl, nameof(CatalogControlBaseUrl));
        ValidateHttpUri(AnalyticsControlBaseUrl, nameof(AnalyticsControlBaseUrl));
        ValidateHttpUri(QueryBaseUrl, nameof(QueryBaseUrl));
        ValidateHttpUri(AnalyticsBaseUrl, nameof(AnalyticsBaseUrl));
        ValidateHttpUri(PromotionOverlayBaseUrl, nameof(PromotionOverlayBaseUrl));
        if (string.IsNullOrWhiteSpace(AcceptanceKey) || AcceptanceKey.Length < 32)
        {
            throw new InvalidOperationException(
                "Acceptance key must contain at least 32 characters.");
        }

        if (Timeout < TimeSpan.FromSeconds(30) || Timeout > TimeSpan.FromMinutes(15))
        {
            throw new InvalidOperationException(
                "Acceptance timeout must be between 30 seconds and 15 minutes.");
        }
    }

    private static void ValidateHttpUri(Uri value, string name)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.IsAbsoluteUri || value.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException($"Acceptance URI '{name}' must use HTTP or HTTPS.");
        }
    }
}
