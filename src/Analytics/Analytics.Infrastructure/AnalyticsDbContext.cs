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

    internal DbSet<AnalyticsListingAccessProjectionRow> ListingAccessProjections =>
        Set<AnalyticsListingAccessProjectionRow>();

    internal DbSet<AnalyticsDailyListingMetricRow> DailyListingMetrics =>
        Set<AnalyticsDailyListingMetricRow>();

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
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.PageContext).HasMaxLength(120);
            entity.Property(row => row.PlacementScopeKey).HasMaxLength(200);
            entity.Property(row => row.PayloadDigest).HasMaxLength(64).IsFixedLength();
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
            entity.HasIndex(row => row.ListingId);
        });

        modelBuilder.Entity<AnalyticsPublicSponsoredPlacementReferenceRow>(entity =>
        {
            entity.ToTable("public_sponsored_placement_reference", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_public_placement_scope",
                    "scope_type BETWEEN 1 AND 4");
                table.HasCheckConstraint(
                    "ck_analytics_public_placement_interval",
                    "starts_at_utc < hard_expiry_at_utc");
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
            entity.HasIndex(row => new { row.PublicReadRevisionId, row.ListingId });
        });

        modelBuilder.Entity<AnalyticsPublicReadActivationCheckpointRow>(entity =>
        {
            entity.ToTable("public_read_activation_checkpoint", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_public_checkpoint_revision",
                    "activation_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_public_checkpoint_digest",
                    "projection_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.CatalogKey);
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.ProjectionDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.ActivationRevision).IsConcurrencyToken();
            entity.HasOne(row => row.PublicReadReference)
                .WithMany()
                .HasForeignKey(row => row.PublicReadRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AnalyticsInboxMessageRow>(entity =>
        {
            entity.ToTable("inbox_message", "messaging", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_inbox_payload_digest",
                    "payload_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_inbox_activation_revision",
                    "activation_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_inbox_disposition",
                    "disposition BETWEEN 1 AND 3");
                table.HasCheckConstraint(
                    "ck_analytics_inbox_result_digest",
                    "result_projection_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_inbox_processing_time",
                    "processed_at_utc >= received_at_utc");
            });
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.RoutingKey).HasMaxLength(200);
            entity.Property(row => row.ContractIdentity).HasMaxLength(200);
            entity.Property(row => row.PayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.ResultProjectionDigest).HasMaxLength(64).IsFixedLength();
            entity.HasOne(row => row.PublicReadReference)
                .WithMany()
                .HasForeignKey(row => row.PublicReadRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new
            {
                row.CatalogKey,
                row.ActivationRevision,
                row.MessageId,
            });
        });

        modelBuilder.Entity<AnalyticsListingAccessProjectionRow>(entity =>
        {
            entity.ToTable("listing_access_projection", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_listing_access_revision",
                    "source_aggregate_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_listing_access_digest",
                    "source_payload_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => new { row.ListingId, row.ActorId });
            entity.Property(row => row.SourceAggregateRevision).IsConcurrencyToken();
            entity.Property(row => row.SourcePayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(row => new { row.ActorId, row.CanViewAnalytics });
        });

        modelBuilder.Entity<AnalyticsDailyListingMetricRow>(entity =>
        {
            entity.ToTable("daily_listing_metric", "aggregates", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_source_count",
                    "source_read_revision_count >= 0");
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_readiness",
                    "readiness_state BETWEEN 1 AND 4");
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_digest",
                    "aggregation_source_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_value_shape",
                    "(readiness_state = 1 AND unavailable_reason IS NULL AND organic_impressions IS NOT NULL AND sponsored_impressions IS NOT NULL AND listing_opens IS NOT NULL AND website_clicks IS NOT NULL AND phone_clicks IS NOT NULL AND whats_app_clicks IS NOT NULL AND email_clicks IS NOT NULL AND map_clicks IS NOT NULL AND external_profile_clicks IS NOT NULL) OR (readiness_state <> 1 AND length(btrim(unavailable_reason)) > 0 AND organic_impressions IS NULL AND sponsored_impressions IS NULL AND listing_opens IS NULL AND website_clicks IS NULL AND phone_clicks IS NULL AND whats_app_clicks IS NULL AND email_clicks IS NULL AND map_clicks IS NULL AND external_profile_clicks IS NULL)");
                table.HasCheckConstraint(
                    "ck_analytics_daily_metric_nonnegative",
                    "(organic_impressions IS NULL OR organic_impressions >= 0) AND (sponsored_impressions IS NULL OR sponsored_impressions >= 0) AND (listing_opens IS NULL OR listing_opens >= 0) AND (website_clicks IS NULL OR website_clicks >= 0) AND (phone_clicks IS NULL OR phone_clicks >= 0) AND (whats_app_clicks IS NULL OR whats_app_clicks >= 0) AND (email_clicks IS NULL OR email_clicks >= 0) AND (map_clicks IS NULL OR map_clicks >= 0) AND (external_profile_clicks IS NULL OR external_profile_clicks >= 0)");
            });
            entity.HasKey(row => new { row.MetricDate, row.CatalogKey, row.ListingId });
            entity.Property(row => row.CatalogKey).HasMaxLength(100);
            entity.Property(row => row.AggregationSourceDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.UnavailableReason).HasMaxLength(1000);
            entity.HasIndex(row => new { row.ListingId, row.MetricDate });
        });

        ApplySnakeCaseColumns(modelBuilder);
    }

    private static void ApplySnakeCaseColumns(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
