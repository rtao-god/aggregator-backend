using System.Text.RegularExpressions;

namespace Aggregator.Analytics.Domain;

public enum AnalyticsObservationKind
{
    Impression = 1,
    DetailView = 2,
    ExternalClick = 3,
    Lead = 4,
    Conversion = 5,
}

/// <summary>One immutable, privacy-bounded interaction accepted by the Analytics owner.</summary>
public sealed record AnalyticsObservation
{
    private AnalyticsObservation(
        Guid id,
        string catalogKey,
        Guid publicReadRevisionId,
        Guid listingId,
        AnalyticsObservationKind kind,
        string placementKey,
        string route,
        string? anonymousSessionHash,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc)
    {
        Id = id;
        CatalogKey = catalogKey;
        PublicReadRevisionId = publicReadRevisionId;
        ListingId = listingId;
        Kind = kind;
        PlacementKey = placementKey;
        Route = route;
        AnonymousSessionHash = anonymousSessionHash;
        OccurredAtUtc = occurredAtUtc;
        ReceivedAtUtc = receivedAtUtc;
    }

    public Guid Id { get; }

    public string CatalogKey { get; }

    public Guid PublicReadRevisionId { get; }

    public Guid ListingId { get; }

    public AnalyticsObservationKind Kind { get; }

    public string PlacementKey { get; }

    public string Route { get; }

    public string? AnonymousSessionHash { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public static AnalyticsObservation Create(
        Guid id,
        string catalogKey,
        Guid publicReadRevisionId,
        Guid listingId,
        AnalyticsObservationKind kind,
        string placementKey,
        string route,
        string? anonymousSessionHash,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc)
    {
        RequireId(id, nameof(id));
        RequireKey(catalogKey, nameof(catalogKey));
        RequireId(publicReadRevisionId, nameof(publicReadRevisionId));
        RequireId(listingId, nameof(listingId));
        if (!Enum.IsDefined(kind))
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_OBSERVATION_KIND_INVALID",
                "The interaction kind is unsupported.");
        }

        RequireKey(placementKey, nameof(placementKey));
        RequireRoute(route);
        if (anonymousSessionHash is not null && !DigestRegex().IsMatch(anonymousSessionHash))
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_SESSION_HASH_INVALID",
                "Anonymous session identity must be a lowercase SHA-256 digest when supplied.");
        }

        RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        if (occurredAtUtc > receivedAtUtc + TimeSpan.FromMinutes(5))
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_OBSERVATION_FROM_FUTURE",
                "An interaction cannot occur materially after it was received.");
        }

        if (occurredAtUtc < receivedAtUtc - TimeSpan.FromDays(7))
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_OBSERVATION_TOO_OLD",
                "An interaction older than the bounded acceptance window is rejected.");
        }

        return new AnalyticsObservation(
            id,
            catalogKey,
            publicReadRevisionId,
            listingId,
            kind,
            placementKey,
            route,
            anonymousSessionHash,
            occurredAtUtc,
            receivedAtUtc);
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_ID_REQUIRED",
                $"A non-empty {name} is required.");
        }
    }

    private static void RequireKey(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !KeyRegex().IsMatch(value))
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_KEY_INVALID",
                $"{name} must be a lowercase semantic key.");
        }
    }

    private static void RequireRoute(string route)
    {
        if (string.IsNullOrWhiteSpace(route) || route.Length > 512 ||
            route[0] != '/' || route.Contains("//", StringComparison.Ordinal) ||
            route.Contains('\\') || route.Contains("..", StringComparison.Ordinal))
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_ROUTE_INVALID",
                "The public route must be absolute, bounded and traversal-free.");
        }
    }

    private static void RequireUtc(DateTimeOffset value, string name)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new AnalyticsObservationException(
                "ANALYTICS_TIME_NOT_UTC",
                $"{name} must use UTC.");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyRegex();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestRegex();
}

public sealed record AnalyticsDailyMetric(
    string CatalogKey,
    Guid PublicReadRevisionId,
    Guid ListingId,
    string PlacementKey,
    DateOnly MetricDate,
    long ImpressionCount,
    long DetailViewCount,
    long ExternalClickCount,
    long LeadCount,
    long ConversionCount,
    DateTimeOffset CalculatedAtUtc,
    long AggregateRevision);

public sealed class AnalyticsObservationException : InvalidOperationException
{
    public AnalyticsObservationException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}
