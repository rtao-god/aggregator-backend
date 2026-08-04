using System.Security.Cryptography;
using System.Text.Json;
using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Aggregator.Analytics.Application;

public sealed record AnalyticsObservationWriteResult(
    Guid ObservationId,
    string RequestDigest,
    DateTimeOffset AcceptedAtUtc,
    bool Replayed);

public interface IAnalyticsRuntimeStore
{
    public Task<AnalyticsObservationWriteResult> RecordAsync(
        AnalyticsObservation observation,
        string requestDigest,
        CancellationToken cancellationToken);

    public Task<IReadOnlyList<AnalyticsDailyMetric>> ReadMetricsAsync(
        string catalogKey,
        Guid publicReadRevisionId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken);

    public Task<int> AggregatePendingAsync(
        int maximumObservationCount,
        DateTimeOffset calculatedAtUtc,
        CancellationToken cancellationToken);
}

public sealed class RecordAnalyticsObservationService(
    IAnalyticsRuntimeStore store,
    TimeProvider timeProvider)
{
    public async Task<AnalyticsObservationReceipt> RecordAsync(
        RecordAnalyticsObservationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receivedAtUtc = timeProvider.GetUtcNow();
        var observation = AnalyticsObservation.Create(
            request.ObservationId,
            request.CatalogKey,
            request.PublicReadRevisionId,
            request.ListingId,
            ToDomain(request.Kind),
            request.PlacementKey,
            request.Route,
            request.AnonymousSessionHash,
            request.OccurredAtUtc,
            receivedAtUtc);
        var requestDigest = AnalyticsRuntimeRequestHash.Compute(request);
        var result = await store.RecordAsync(observation, requestDigest, cancellationToken);
        return new AnalyticsObservationReceipt(
            result.ObservationId,
            result.Replayed,
            result.AcceptedAtUtc);
    }

    private static AnalyticsObservationKind ToDomain(AnalyticsObservationKindContract value) => value switch
    {
        AnalyticsObservationKindContract.Impression => AnalyticsObservationKind.Impression,
        AnalyticsObservationKindContract.DetailView => AnalyticsObservationKind.DetailView,
        AnalyticsObservationKindContract.ExternalClick => AnalyticsObservationKind.ExternalClick,
        AnalyticsObservationKindContract.Lead => AnalyticsObservationKind.Lead,
        AnalyticsObservationKindContract.Conversion => AnalyticsObservationKind.Conversion,
        _ => throw new AnalyticsRuntimeException(
            "Analytics.Contracts",
            "ANALYTICS_OBSERVATION_KIND_INVALID",
            422,
            "The interaction kind is unsupported.",
            "Submit one of the documented string enum values."),
    };
}

public sealed class ReadAnalyticsMetricsService(
    IAnalyticsRuntimeStore store,
    TimeProvider timeProvider)
{
    public async Task<AnalyticsMetricsResponse> ReadAsync(
        string catalogKey,
        Guid publicReadRevisionId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(catalogKey))
        {
            throw new AnalyticsRuntimeException(
                "Analytics.Contracts",
                "ANALYTICS_CATALOG_KEY_REQUIRED",
                400,
                "A catalog key is required.",
                "Provide the exact public catalog key.");
        }

        if (publicReadRevisionId == Guid.Empty)
        {
            throw new AnalyticsRuntimeException(
                "Analytics.Contracts",
                "ANALYTICS_PUBLIC_READ_REVISION_REQUIRED",
                400,
                "A public-read revision is required.",
                "Provide the exact Query public-read revision.");
        }

        if (fromDate > toDate || toDate.DayNumber - fromDate.DayNumber > 366)
        {
            throw new AnalyticsRuntimeException(
                "Analytics.Contracts",
                "ANALYTICS_DATE_RANGE_INVALID",
                400,
                "The metric date range must be ordered and no longer than 367 days.",
                "Request a bounded ordered UTC date range.");
        }

        var metrics = await store.ReadMetricsAsync(
            catalogKey,
            publicReadRevisionId,
            fromDate,
            toDate,
            cancellationToken);
        var items = metrics
            .OrderBy(metric => metric.MetricDate)
            .ThenBy(metric => metric.ListingId)
            .ThenBy(metric => metric.PlacementKey, StringComparer.Ordinal)
            .Select(metric => new AnalyticsMetricItem(
                metric.ListingId,
                metric.PlacementKey,
                metric.MetricDate,
                metric.ImpressionCount,
                metric.DetailViewCount,
                metric.ExternalClickCount,
                metric.LeadCount,
                metric.ConversionCount,
                metric.AggregateRevision))
            .ToArray();
        return new AnalyticsMetricsResponse(
            catalogKey,
            publicReadRevisionId,
            fromDate,
            toDate,
            items,
            timeProvider.GetUtcNow());
    }
}

public sealed class AggregateAnalyticsObservationsService(
    IAnalyticsRuntimeStore store,
    TimeProvider timeProvider)
{
    public Task<int> AggregateAsync(int maximumObservationCount, CancellationToken cancellationToken)
    {
        if (maximumObservationCount is < 1 or > 10_000)
        {
            throw new AnalyticsRuntimeException(
                "Analytics.Aggregation",
                "ANALYTICS_AGGREGATION_BATCH_INVALID",
                500,
                "The aggregation batch size must be between 1 and 10000.",
                "Correct the Analytics worker configuration.");
        }

        return store.AggregatePendingAsync(
            maximumObservationCount,
            timeProvider.GetUtcNow(),
            cancellationToken);
    }
}

public static class AnalyticsRuntimeApplicationExtensions
{
    public static IServiceCollection AddAnalyticsRuntimeApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<RecordAnalyticsObservationService>();
        services.AddScoped<ReadAnalyticsMetricsService>();
        services.AddScoped<AggregateAnalyticsObservationsService>();
        return services;
    }
}

public static class AnalyticsRuntimeRequestHash
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string Compute(RecordAnalyticsObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

public sealed class AnalyticsRuntimeException : InvalidOperationException
{
    public AnalyticsRuntimeException(
        string owner,
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(requiredAction);
        Owner = owner;
        Code = code;
        StatusCode = statusCode;
        RequiredAction = requiredAction;
        Context = context ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    public string Owner { get; }

    public string Code { get; }

    public int StatusCode { get; }

    public string RequiredAction { get; }

    public IReadOnlyDictionary<string, object?> Context { get; }
}
