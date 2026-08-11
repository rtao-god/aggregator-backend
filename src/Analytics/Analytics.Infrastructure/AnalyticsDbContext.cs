using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Analytics.Infrastructure;

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    internal DbSet<AnalyticsInteractionEventRow> InteractionEvents => Set<AnalyticsInteractionEventRow>();

    internal DbSet<AnalyticsInteractionCampaignParameterRow> InteractionCampaignParameters =>
        Set<AnalyticsInteractionCampaignParameterRow>();

    internal DbSet<AnalyticsPublicReadReferenceRow> PublicReadReferences =>
        Set<AnalyticsPublicReadReferenceRow>();

    internal DbSet<AnalyticsPublicListingReferenceRow> PublicListingReferences =>
        Set<AnalyticsPublicListingReferenceRow>();

    internal DbSet<AnalyticsPublicSponsoredPlacementReferenceRow> PublicSponsoredPlacementReferences =>
        Set<AnalyticsPublicSponsoredPlacementReferenceRow>();

    internal DbSet<AnalyticsPublicReadActivationCheckpointRow> PublicReadActivationCheckpoints =>
        Set<AnalyticsPublicReadActivationCheckpointRow>();

    internal DbSet<AnalyticsInboxMessageRow> PublicReadInboxMessages =>
        Set<AnalyticsInboxMessageRow>();

    internal DbSet<AnalyticsDailyListingMetricRow> DailyListingMetrics =>
        Set<AnalyticsDailyListingMetricRow>();

    internal DbSet<AnalyticsAggregateRunRow> AggregateRuns =>
        Set<AnalyticsAggregateRunRow>();

    internal DbSet<AnalyticsAggregateRunItemRow> AggregateRunItems =>
        Set<AnalyticsAggregateRunItemRow>();

    internal DbSet<AnalyticsAggregateReadinessRow> AggregateReadiness =>
        Set<AnalyticsAggregateReadinessRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AnalyticsInteractionEventRow>(entity =>
        {
            entity.ToTable("interaction_event", "events", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_kind",
                    "event_kind BETWEEN 1 AND 11");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_listing_shape",
                    "(event_kind = 1 AND listing_id IS NULL) OR (event_kind BETWEEN 2 AND 11 AND listing_id IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_placement_kind",
                    "placement_exposure_kind BETWEEN 1 AND 3");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_placement_shape",
                    "(placement_exposure_kind = 2 AND placement_id IS NOT NULL) OR (placement_exposure_kind <> 2 AND placement_id IS NULL)");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_context_enums",
                    "referrer_class BETWEEN 1 AND 7 AND consent_mode BETWEEN 1 AND 2 AND quality_state BETWEEN 1 AND 4");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_time_bounds",
                    "occurred_at_utc >= received_at_utc - INTERVAL '7 days' AND occurred_at_utc <= received_at_utc + INTERVAL '5 minutes'");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_digest",
                    "payload_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_retention_state",
                    "retention_state IN (1, 2)");
                table.HasCheckConstraint(
                    "ck_analytics_interaction_event_retention_shape",
                    "(retention_state = 1 AND retained_at_utc IS NULL AND retention_operation_id IS NULL) OR " +
                    "(retention_state = 2 AND retained_at_utc IS NOT NULL AND retention_operation_id IS NOT NULL)");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.PageContext).HasMaxLength(120);
            entity.Property(row => row.PlacementScopeKey).HasMaxLength(200);
            entity.Property(row => row.PayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.RetentionState).HasConversion<short>();
            entity.HasOne(row => row.PublicReadReference)
                .WithMany()
                .HasForeignKey(row => row.PublicReadRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.SponsoredPlacementReference)
                .WithMany()
                .HasForeignKey(row => new
                {
                    row.PublicReadRevisionId,
                    row.PlacementId,
                    row.ListingId,
                })
                .HasPrincipalKey(row => new
                {
                    row.PublicReadRevisionId,
                    row.PlacementId,
                    row.ListingId,
                })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new { row.ClientEventId, row.EventKind })
                .IsUnique()
                .HasDatabaseName("ux_analytics_interaction_event_semantic_key");
            entity.HasIndex(row => new { row.CatalogKey, row.ListingId, row.OccurredAtUtc });
            entity.HasIndex(row => row.PublicReadRevisionId);
            entity.HasIndex(row => row.RetentionOperationId)
                .HasFilter("retention_operation_id IS NOT NULL")
                .HasDatabaseName("ix_analytics_interaction_event_retention_operation");
        });

        modelBuilder.Entity<AnalyticsInteractionCampaignParameterRow>(entity =>
        {
            entity.ToTable("interaction_event_campaign_parameter", "events", table =>
                table.HasCheckConstraint(
                    "ck_analytics_campaign_parameter_key",
                    "parameter_key IN ('utm_source', 'utm_medium', 'utm_campaign', 'utm_content', 'utm_term')"));
            entity.HasKey(row => new { row.EventId, row.ParameterKey });
            entity.Property(row => row.ParameterKey).HasMaxLength(32);
            entity.Property(row => row.ParameterValue).HasMaxLength(200);
            entity.HasOne(row => row.Event)
                .WithMany()
                .HasForeignKey(row => row.EventId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnalyticsPublicReadReferenceRow>(entity =>
        {
            entity.ToTable("public_read_reference", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_public_read_activation_revision",
                    "activation_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_public_read_content_digest",
                    "public_read_content_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_public_read_membership_digest",
                    "membership_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_public_read_projection_digest",
                    "projection_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.PublicReadRevisionId);
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.PublicReadContentDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.MembershipDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.ProjectionDigest).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(row => new { row.CatalogKey, row.ActivationRevision })
                .IsUnique();
            entity.HasIndex(row => new { row.CatalogKey, row.ActivatedAtUtc });
        });

        modelBuilder.Entity<AnalyticsPublicListingReferenceRow>(entity =>
        {
            entity.ToTable("public_listing_reference", "access_projection");
            entity.HasKey(row => new { row.PublicReadRevisionId, row.ListingId });
            entity.HasOne(row => row.PublicReadReference)
                .WithMany()
                .HasForeignKey(row => row.PublicReadRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnalyticsPublicSponsoredPlacementReferenceRow>(entity =>
        {
            entity.ToTable("public_sponsored_placement_reference", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_public_sponsored_placement_scope_type",
                    "scope_type BETWEEN 1 AND 4");
                table.HasCheckConstraint(
                    "ck_analytics_public_sponsored_placement_window",
                    "hard_expiry_at_utc > starts_at_utc");
            });
            entity.HasKey(row => new { row.PublicReadRevisionId, row.PlacementId });
            entity.HasAlternateKey(row => new
            {
                row.PublicReadRevisionId,
                row.PlacementId,
                row.ListingId,
            });
            entity.Property(row => row.ScopeKey).HasMaxLength(200);
            entity.HasOne(row => row.PublicReadReference)
                .WithMany()
                .HasForeignKey(row => row.PublicReadRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(row => row.PublicListingReference)
                .WithMany()
                .HasForeignKey(row => new { row.PublicReadRevisionId, row.ListingId })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnalyticsPublicReadActivationCheckpointRow>(entity =>
        {
            entity.ToTable("public_read_activation_checkpoint", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_public_read_checkpoint_revision",
                    "activation_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_public_read_checkpoint_digest",
                    "projection_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.CatalogKey);
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.ProjectionDigest).HasMaxLength(64).IsFixedLength();
            entity.HasOne(row => row.PublicReadReference)
                .WithMany()
                .HasForeignKey(row => row.PublicReadRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnalyticsInboxMessageRow>(entity =>
        {
            entity.ToTable("inbox_message", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_inbox_digest",
                    "payload_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_inbox_activation_revision",
                    "activation_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_inbox_projection_digest",
                    "projection_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.RoutingKey).HasMaxLength(200);
            entity.Property(row => row.ContractIdentity).HasMaxLength(200);
            entity.Property(row => row.PayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.ProjectionDigest).HasMaxLength(64).IsFixedLength();
            entity.HasOne(row => row.PublicReadReference)
                .WithMany()
                .HasForeignKey(row => row.PublicReadRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnalyticsDailyListingMetricRow>(entity =>
        {
            entity.ToTable("daily_listing_metric", "aggregates", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_source_digest",
                    "aggregation_source_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_read_revision_count",
                    "source_read_revision_count > 0");
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_readiness",
                    "readiness_state BETWEEN 1 AND 4");
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_counts",
                    "(readiness_state = 1 AND organic_impressions IS NOT NULL AND sponsored_impressions IS NOT NULL AND listing_opens IS NOT NULL AND website_clicks IS NOT NULL AND phone_clicks IS NOT NULL AND whatsapp_clicks IS NOT NULL AND email_clicks IS NOT NULL AND map_clicks IS NOT NULL AND external_profile_clicks IS NOT NULL AND unavailable_reason IS NULL) OR (readiness_state <> 1 AND organic_impressions IS NULL AND sponsored_impressions IS NULL AND listing_opens IS NULL AND website_clicks IS NULL AND phone_clicks IS NULL AND whatsapp_clicks IS NULL AND email_clicks IS NULL AND map_clicks IS NULL AND external_profile_clicks IS NULL AND unavailable_reason IS NOT NULL)");
            });
            entity.HasKey(row => new { row.MetricDate, row.CatalogKey, row.ListingId });
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.AggregationSourceDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.UnavailableReason).HasMaxLength(500);
        });

        modelBuilder.Entity<AnalyticsAggregateRunRow>(entity =>
        {
            entity.ToTable("aggregate_run", "aggregates", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_run_range",
                    "to_exclusive > from_inclusive");
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_run_state",
                    "state BETWEEN 1 AND 3");
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_run_source_digest",
                    "source_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_run_counts",
                    "materialized_metric_count >= 0 AND removed_stale_metric_count >= 0 AND materialized_day_count >= 0");
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_run_lease",
                    "(state = 1 AND lease_token IS NOT NULL AND lease_owner IS NOT NULL AND lease_expires_at_utc IS NOT NULL AND completed_at_utc IS NULL AND failure_code IS NULL AND failure_detail IS NULL AND required_action IS NULL) OR (state = 2 AND lease_token IS NULL AND lease_owner IS NULL AND lease_expires_at_utc IS NULL AND completed_at_utc IS NOT NULL AND failure_code IS NULL AND failure_detail IS NULL AND required_action IS NULL) OR (state = 3 AND lease_token IS NULL AND lease_owner IS NULL AND lease_expires_at_utc IS NULL AND completed_at_utc IS NOT NULL AND failure_code IS NOT NULL AND failure_detail IS NOT NULL AND required_action IS NOT NULL)");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.SourceDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.LeaseToken).HasMaxLength(128);
            entity.Property(row => row.LeaseOwner).HasMaxLength(200);
            entity.Property(row => row.FailureCode).HasMaxLength(200);
            entity.Property(row => row.FailureDetail).HasMaxLength(2000);
            entity.Property(row => row.RequiredAction).HasMaxLength(1000);
            entity.HasIndex(row => new { row.FromInclusive, row.ToExclusive, row.StartedAtUtc });
        });

        modelBuilder.Entity<AnalyticsAggregateRunItemRow>(entity =>
        {
            entity.ToTable("aggregate_run_item", "aggregates", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_run_item_digest",
                    "source_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_run_item_count",
                    "metric_count >= 0");
            });
            entity.HasKey(row => new { row.RunId, row.MetricDate });
            entity.Property(row => row.SourceDigest).HasMaxLength(64).IsFixedLength();
            entity.HasOne(row => row.Run)
                .WithMany()
                .HasForeignKey(row => row.RunId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnalyticsAggregateReadinessRow>(entity =>
        {
            entity.ToTable("aggregate_readiness", "aggregates", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_readiness_digest",
                    "source_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_aggregate_readiness_count",
                    "metric_count >= 0");
            });
            entity.HasKey(row => row.MetricDate);
            entity.Property(row => row.SourceDigest).HasMaxLength(64).IsFixedLength();
            entity.HasOne(row => row.Run)
                .WithMany()
                .HasForeignKey(row => row.RunId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    public static string ComputeModelDigest()
    {
        var builder = new DbContextOptionsBuilder<AnalyticsDbContext>()
            .UseNpgsql("Host=localhost;Database=analytics_model;Username=model;Password=model");
        using var context = new AnalyticsDbContext(builder.Options);
        var representation = new StringBuilder();
        foreach (var entity in context.Model.GetEntityTypes().OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            representation.Append(entity.Name).Append('\n');
            foreach (var property in entity.GetProperties().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                representation.Append(property.Name)
                    .Append(':')
                    .Append(property.ClrType.FullName)
                    .Append(':')
                    .Append(property.IsNullable)
                    .Append('\n');
            }
        }

        return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(representation.ToString())))
            .ToLowerInvariant();
    }
}
