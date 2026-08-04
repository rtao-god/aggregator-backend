using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aggregator.Analytics.Infrastructure;

public sealed class AnalyticsRuntimeDbContext(DbContextOptions<AnalyticsRuntimeDbContext> options)
    : DbContext(options)
{
    internal DbSet<AnalyticsObservationRow> Observations => Set<AnalyticsObservationRow>();

    internal DbSet<AnalyticsDailyMetricRow> DailyMetrics => Set<AnalyticsDailyMetricRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var observation = modelBuilder.Entity<AnalyticsObservationRow>();
        observation.ToTable("interaction_observation", "analytics");
        observation.HasKey(row => row.Id);
        observation.Property(row => row.Id).HasColumnName("id");
        observation.Property(row => row.CatalogKey).HasColumnName("catalog_key").HasMaxLength(96);
        observation.Property(row => row.PublicReadRevisionId).HasColumnName("public_read_revision_id");
        observation.Property(row => row.ListingId).HasColumnName("listing_id");
        observation.Property(row => row.Kind).HasColumnName("kind");
        observation.Property(row => row.PlacementKey).HasColumnName("placement_key").HasMaxLength(96);
        observation.Property(row => row.Route).HasColumnName("route").HasMaxLength(512);
        observation.Property(row => row.AnonymousSessionHash)
            .HasColumnName("anonymous_session_hash")
            .HasMaxLength(64)
            .IsFixedLength();
        observation.Property(row => row.RequestDigest)
            .HasColumnName("request_digest")
            .HasMaxLength(64)
            .IsFixedLength();
        observation.Property(row => row.OccurredAtUtc)
            .HasColumnName("occurred_at_utc")
            .HasColumnType("timestamp with time zone");
        observation.Property(row => row.ReceivedAtUtc)
            .HasColumnName("received_at_utc")
            .HasColumnType("timestamp with time zone");
        observation.Property(row => row.AggregatedAtUtc)
            .HasColumnName("aggregated_at_utc")
            .HasColumnType("timestamp with time zone");
        observation.HasIndex(row => new { row.AggregatedAtUtc, row.ReceivedAtUtc, row.Id });
        observation.HasIndex(row => new
        {
            row.CatalogKey,
            row.PublicReadRevisionId,
            row.ListingId,
            row.OccurredAtUtc,
        });
        observation.ToTable(table =>
        {
            table.HasCheckConstraint("ck_analytics_observation_kind", "kind BETWEEN 1 AND 5");
            table.HasCheckConstraint(
                "ck_analytics_observation_time_order",
                "occurred_at_utc <= received_at_utc + interval '5 minutes'");
            table.HasCheckConstraint(
                "ck_analytics_observation_request_digest",
                "request_digest ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_analytics_observation_session_hash",
                "anonymous_session_hash IS NULL OR anonymous_session_hash ~ '^[0-9a-f]{64}$'");
        });

        var metric = modelBuilder.Entity<AnalyticsDailyMetricRow>();
        metric.ToTable("daily_listing_metric", "analytics");
        metric.HasKey(row => new
        {
            row.CatalogKey,
            row.PublicReadRevisionId,
            row.ListingId,
            row.PlacementKey,
            row.MetricDate,
        });
        metric.Property(row => row.CatalogKey).HasColumnName("catalog_key").HasMaxLength(96);
        metric.Property(row => row.PublicReadRevisionId).HasColumnName("public_read_revision_id");
        metric.Property(row => row.ListingId).HasColumnName("listing_id");
        metric.Property(row => row.PlacementKey).HasColumnName("placement_key").HasMaxLength(96);
        metric.Property(row => row.MetricDate).HasColumnName("metric_date").HasColumnType("date");
        metric.Property(row => row.ImpressionCount).HasColumnName("impression_count");
        metric.Property(row => row.DetailViewCount).HasColumnName("detail_view_count");
        metric.Property(row => row.ExternalClickCount).HasColumnName("external_click_count");
        metric.Property(row => row.LeadCount).HasColumnName("lead_count");
        metric.Property(row => row.ConversionCount).HasColumnName("conversion_count");
        metric.Property(row => row.CalculatedAtUtc)
            .HasColumnName("calculated_at_utc")
            .HasColumnType("timestamp with time zone");
        metric.Property(row => row.AggregateRevision)
            .HasColumnName("aggregate_revision")
            .IsConcurrencyToken();
        metric.HasIndex(row => new { row.CatalogKey, row.PublicReadRevisionId, row.MetricDate });
        metric.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_analytics_daily_metric_counts",
                "impression_count >= 0 AND detail_view_count >= 0 AND external_click_count >= 0 AND lead_count >= 0 AND conversion_count >= 0");
            table.HasCheckConstraint(
                "ck_analytics_daily_metric_revision",
                "aggregate_revision > 0");
        });
    }
}

public sealed class EfAnalyticsRuntimeStore(AnalyticsRuntimeDbContext dbContext)
    : IAnalyticsRuntimeStore
{
    public async Task<AnalyticsObservationWriteResult> RecordAsync(
        AnalyticsObservation observation,
        string requestDigest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateDigest(requestDigest);
        var existing = await dbContext.Observations
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == observation.Id, cancellationToken);
        if (existing is not null)
        {
            return Replay(existing, requestDigest);
        }

        dbContext.Observations.Add(ToRow(observation, requestDigest));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new AnalyticsObservationWriteResult(
                observation.Id,
                requestDigest,
                observation.ReceivedAtUtc,
                Replayed: false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            existing = await dbContext.Observations
                .AsNoTracking()
                .SingleAsync(row => row.Id == observation.Id, cancellationToken);
            return Replay(existing, requestDigest);
        }
    }

    public async Task<IReadOnlyList<AnalyticsDailyMetric>> ReadMetricsAsync(
        string catalogKey,
        Guid publicReadRevisionId,
        DateOnly fromDate,
        DateOnly toDate,
        CancellationToken cancellationToken) =>
        await dbContext.DailyMetrics
            .AsNoTracking()
            .Where(row =>
                row.CatalogKey == catalogKey &&
                row.PublicReadRevisionId == publicReadRevisionId &&
                row.MetricDate >= fromDate &&
                row.MetricDate <= toDate)
            .OrderBy(row => row.MetricDate)
            .ThenBy(row => row.ListingId)
            .ThenBy(row => row.PlacementKey)
            .Select(row => new AnalyticsDailyMetric(
                row.CatalogKey,
                row.PublicReadRevisionId,
                row.ListingId,
                row.PlacementKey,
                row.MetricDate,
                row.ImpressionCount,
                row.DetailViewCount,
                row.ExternalClickCount,
                row.LeadCount,
                row.ConversionCount,
                row.CalculatedAtUtc,
                row.AggregateRevision))
            .ToArrayAsync(cancellationToken);

    public async Task<int> AggregatePendingAsync(
        int maximumObservationCount,
        DateTimeOffset calculatedAtUtc,
        CancellationToken cancellationToken)
    {
        if (calculatedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new AnalyticsRuntimeException(
                "Analytics.Aggregation",
                "ANALYTICS_CALCULATION_TIME_NOT_UTC",
                500,
                "The aggregation timestamp must use UTC.",
                "Correct the worker clock configuration.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.ReadCommitted,
            cancellationToken);
        var observations = await dbContext.Observations
            .FromSqlInterpolated($$"""
                SELECT *
                FROM analytics.interaction_observation
                WHERE aggregated_at_utc IS NULL
                ORDER BY received_at_utc, id
                FOR UPDATE SKIP LOCKED
                LIMIT {{maximumObservationCount}}
                """)
            .ToArrayAsync(cancellationToken);
        foreach (var group in observations.GroupBy(row => new
        {
            row.CatalogKey,
            row.PublicReadRevisionId,
            row.ListingId,
            row.PlacementKey,
            MetricDate = DateOnly.FromDateTime(row.OccurredAtUtc.UtcDateTime),
        }))
        {
            var metric = await dbContext.DailyMetrics.SingleOrDefaultAsync(
                row =>
                    row.CatalogKey == group.Key.CatalogKey &&
                    row.PublicReadRevisionId == group.Key.PublicReadRevisionId &&
                    row.ListingId == group.Key.ListingId &&
                    row.PlacementKey == group.Key.PlacementKey &&
                    row.MetricDate == group.Key.MetricDate,
                cancellationToken);
            if (metric is null)
            {
                metric = new AnalyticsDailyMetricRow
                {
                    CatalogKey = group.Key.CatalogKey,
                    PublicReadRevisionId = group.Key.PublicReadRevisionId,
                    ListingId = group.Key.ListingId,
                    PlacementKey = group.Key.PlacementKey,
                    MetricDate = group.Key.MetricDate,
                    CalculatedAtUtc = calculatedAtUtc,
                    AggregateRevision = 1,
                };
                dbContext.DailyMetrics.Add(metric);
            }
            else
            {
                metric.AggregateRevision++;
                metric.CalculatedAtUtc = calculatedAtUtc;
            }

            metric.ImpressionCount += group.LongCount(row => row.Kind == (int)AnalyticsObservationKind.Impression);
            metric.DetailViewCount += group.LongCount(row => row.Kind == (int)AnalyticsObservationKind.DetailView);
            metric.ExternalClickCount += group.LongCount(row => row.Kind == (int)AnalyticsObservationKind.ExternalClick);
            metric.LeadCount += group.LongCount(row => row.Kind == (int)AnalyticsObservationKind.Lead);
            metric.ConversionCount += group.LongCount(row => row.Kind == (int)AnalyticsObservationKind.Conversion);
        }

        foreach (var observation in observations)
        {
            observation.AggregatedAtUtc = calculatedAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return observations.Length;
    }

    private static AnalyticsObservationWriteResult Replay(
        AnalyticsObservationRow existing,
        string requestDigest)
    {
        if (!string.Equals(existing.RequestDigest, requestDigest, StringComparison.Ordinal))
        {
            throw new AnalyticsRuntimeException(
                "Analytics.Interactions",
                "ANALYTICS_OBSERVATION_ID_CONFLICT",
                409,
                "The observation ID was already registered with a different request digest.",
                "Replay the exact original request or submit a new observation ID.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["observationId"] = existing.Id,
                    ["existingRequestDigest"] = existing.RequestDigest,
                    ["actualRequestDigest"] = requestDigest,
                });
        }

        return new AnalyticsObservationWriteResult(
            existing.Id,
            existing.RequestDigest,
            existing.ReceivedAtUtc,
            Replayed: true);
    }

    private static AnalyticsObservationRow ToRow(
        AnalyticsObservation observation,
        string requestDigest) =>
        new()
        {
            Id = observation.Id,
            CatalogKey = observation.CatalogKey,
            PublicReadRevisionId = observation.PublicReadRevisionId,
            ListingId = observation.ListingId,
            Kind = (int)observation.Kind,
            PlacementKey = observation.PlacementKey,
            Route = observation.Route,
            AnonymousSessionHash = observation.AnonymousSessionHash,
            RequestDigest = requestDigest,
            OccurredAtUtc = observation.OccurredAtUtc,
            ReceivedAtUtc = observation.ReceivedAtUtc,
        };

    private static void ValidateDigest(string digest)
    {
        if (digest is not { Length: 64 } ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new AnalyticsRuntimeException(
                "Analytics.Persistence",
                "ANALYTICS_REQUEST_DIGEST_INVALID",
                500,
                "The request digest is invalid.",
                "Correct canonical request hashing before persistence.");
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };

    public Task<AnalyticsInteractionRegistration> RegisterAsync(AnalyticsInteractionRecord interaction, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<AnalyticsListingMetricsSnapshot?> ReadListingMetricsAsync(string catalogKey, Guid listingId, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

public static class AnalyticsRuntimeInfrastructureExtensions
{
    public static IServiceCollection AddAnalyticsRuntimeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Analytics")
            ?? throw new InvalidOperationException("Connection string 'Analytics' is required.");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Analytics' cannot be empty.");
        }

        services.AddDbContext<AnalyticsRuntimeDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<IAnalyticsRuntimeStore, EfAnalyticsRuntimeStore>();
        services.AddScoped<AnalyticsRuntimeReadinessProbe>();
        return services;
    }
}

public sealed class AnalyticsRuntimeReadinessProbe(AnalyticsRuntimeDbContext dbContext)
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
        dbContext.Database.CanConnectAsync(cancellationToken);
}

internal sealed class AnalyticsObservationRow
{
    public Guid Id { get; set; }

    public string CatalogKey { get; set; } = string.Empty;

    public Guid PublicReadRevisionId { get; set; }

    public Guid ListingId { get; set; }

    public int Kind { get; set; }

    public string PlacementKey { get; set; } = string.Empty;

    public string Route { get; set; } = string.Empty;

    public string? AnonymousSessionHash { get; set; }

    public string RequestDigest { get; set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public DateTimeOffset? AggregatedAtUtc { get; set; }
}

internal sealed class AnalyticsDailyMetricRow
{
    public string CatalogKey { get; set; } = string.Empty;

    public Guid PublicReadRevisionId { get; set; }

    public Guid ListingId { get; set; }

    public string PlacementKey { get; set; } = string.Empty;

    public DateOnly MetricDate { get; set; }

    public long ImpressionCount { get; set; }

    public long DetailViewCount { get; set; }

    public long ExternalClickCount { get; set; }

    public long LeadCount { get; set; }

    public long ConversionCount { get; set; }

    public DateTimeOffset CalculatedAtUtc { get; set; }

    public long AggregateRevision { get; set; }
}
