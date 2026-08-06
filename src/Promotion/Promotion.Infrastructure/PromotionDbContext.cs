using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Promotion.Infrastructure;

public sealed class PromotionDbContext(DbContextOptions<PromotionDbContext> options) : DbContext(options)
{
    internal DbSet<PromotionProductRow> Products => Set<PromotionProductRow>();

    internal DbSet<PromotionProductRevisionRow> ProductRevisions => Set<PromotionProductRevisionRow>();

    internal DbSet<PromotionEntitlementRow> Entitlements => Set<PromotionEntitlementRow>();

    internal DbSet<SponsoredPlacementRow> Placements => Set<SponsoredPlacementRow>();

    internal DbSet<SponsoredPlacementRevisionRow> PlacementRevisions => Set<SponsoredPlacementRevisionRow>();

    internal DbSet<SponsoredPlacementCapacityRow> PlacementCapacity => Set<SponsoredPlacementCapacityRow>();

    internal DbSet<ListingPromotionEligibilityRow> ListingEligibility => Set<ListingPromotionEligibilityRow>();

    internal DbSet<PromotionCommandRow> Commands => Set<PromotionCommandRow>();

    internal DbSet<PromotionOutboxRow> OutboxMessages => Set<PromotionOutboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<PromotionProductRow>(entity =>
        {
            entity.ToTable("promotion_product", "products", table =>
            {
                table.HasCheckConstraint("ck_promotion_product_state", "state BETWEEN 1 AND 3");
                table.HasCheckConstraint("ck_promotion_product_revision", "aggregate_revision > 0");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ProductKey).HasMaxLength(120);
            entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
            entity.HasIndex(row => row.ProductKey).IsUnique();
        });

        modelBuilder.Entity<PromotionProductRevisionRow>(entity =>
        {
            entity.ToTable("promotion_product_revision", "products", table =>
            {
                table.HasCheckConstraint("ck_promotion_product_revision_number", "revision_number > 0");
                table.HasCheckConstraint(
                    "ck_promotion_product_revision_digest",
                    "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.DisplayNamesJson).HasColumnType("jsonb");
            entity.Property(row => row.PresentationFeaturesJson).HasColumnType("jsonb");
            entity.Property(row => row.RequiredContactCapability).HasMaxLength(120);
            entity.Property(row => row.ContentDigest).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(row => new { row.ProductId, row.RevisionNumber }).IsUnique();
            entity.HasOne<PromotionProductRow>()
                .WithMany()
                .HasForeignKey(row => row.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PromotionEntitlementRow>(entity =>
        {
            entity.ToTable("promotion_entitlement", "entitlements", table =>
            {
                table.HasCheckConstraint("ck_promotion_entitlement_source", "source_type BETWEEN 1 AND 3");
                table.HasCheckConstraint("ck_promotion_entitlement_state", "state BETWEEN 1 AND 5");
                table.HasCheckConstraint("ck_promotion_entitlement_window", "ends_at_utc > starts_at_utc");
                table.HasCheckConstraint("ck_promotion_entitlement_revision", "aggregate_revision > 0");
                table.HasCheckConstraint("ck_promotion_entitlement_time", "changed_at_utc >= created_at_utc");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ProductKey).HasMaxLength(120);
            entity.Property(row => row.ExternalReference).HasMaxLength(500);
            entity.Property(row => row.AuditReason).HasMaxLength(2000);
            entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.ListingId, row.State, row.StartsAtUtc, row.EndsAtUtc });
            entity.HasIndex(row => row.ProductKey);
        });

        modelBuilder.Entity<SponsoredPlacementRow>(entity =>
        {
            entity.ToTable("sponsored_placement", "placements", table =>
            {
                table.HasCheckConstraint("ck_sponsored_placement_state", "state BETWEEN 1 AND 5");
                table.HasCheckConstraint("ck_sponsored_placement_revision", "aggregate_revision > 0");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ProductKey).HasMaxLength(120);
            entity.Property(row => row.AuditReason).HasMaxLength(2000);
            entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
            entity.HasIndex(row => row.EntitlementId);
            entity.HasIndex(row => row.ListingId);
            entity.HasOne<PromotionEntitlementRow>()
                .WithMany()
                .HasForeignKey(row => row.EntitlementId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SponsoredPlacementRevisionRow>(entity =>
        {
            entity.ToTable("sponsored_placement_revision", "placements", table =>
            {
                table.HasCheckConstraint("ck_sponsored_placement_revision_number", "revision_number > 0");
                table.HasCheckConstraint("ck_sponsored_placement_scope", "scope_type BETWEEN 1 AND 4");
                table.HasCheckConstraint("ck_sponsored_placement_window", "ends_at_utc > starts_at_utc");
                table.HasCheckConstraint("ck_sponsored_placement_priority", "priority_band BETWEEN 0 AND 1000");
                table.HasCheckConstraint("ck_sponsored_placement_slot", "capacity_slot BETWEEN 1 AND 1000");
                table.HasCheckConstraint(
                    "ck_sponsored_placement_digest",
                    "content_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(120);
            entity.Property(row => row.ScopeKey).HasMaxLength(120);
            entity.Property(row => row.LocaleScopeJson).HasColumnType("jsonb");
            entity.Property(row => row.PresentationLabelKey).HasMaxLength(120);
            entity.Property(row => row.ContentDigest).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(row => new { row.PlacementId, row.RevisionNumber }).IsUnique();
            entity.HasOne<SponsoredPlacementRow>()
                .WithMany()
                .HasForeignKey(row => row.PlacementId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SponsoredPlacementCapacityRow>(entity =>
        {
            entity.ToTable("sponsored_placement_capacity", "placements", table =>
            {
                table.HasCheckConstraint("ck_sponsored_capacity_scope", "scope_type BETWEEN 1 AND 4");
                table.HasCheckConstraint("ck_sponsored_capacity_slot", "capacity_slot BETWEEN 1 AND 1000");
                table.HasCheckConstraint("ck_sponsored_capacity_window", "ends_at_utc > starts_at_utc");
                table.HasCheckConstraint("ck_sponsored_capacity_state", "placement_state IN (1, 2)");
            });
            entity.HasKey(row => new { row.PlacementId, row.Locale });
            entity.Property(row => row.CatalogKey).HasMaxLength(120);
            entity.Property(row => row.ScopeKey).HasMaxLength(120);
            entity.Property(row => row.Locale).HasMaxLength(35);
            entity.HasIndex(row => new
            {
                row.CatalogKey,
                row.ScopeType,
                row.ScopeKey,
                row.Locale,
                row.CapacitySlot,
                row.StartsAtUtc,
                row.EndsAtUtc,
            });
            entity.HasOne<SponsoredPlacementRow>()
                .WithMany()
                .HasForeignKey(row => row.PlacementId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SponsoredPlacementRevisionRow>()
                .WithMany()
                .HasForeignKey(row => row.PlacementRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ListingPromotionEligibilityRow>(entity =>
        {
            entity.ToTable("listing_eligibility_projection", "access_projection", table =>
            {
                table.HasCheckConstraint("ck_promotion_eligibility_revision", "source_revision > 0");
                table.HasCheckConstraint(
                    "ck_promotion_eligibility_state",
                    "NOT (is_archived AND is_published)");
            });
            entity.HasKey(row => new { row.CatalogKey, row.ListingId });
            entity.Property(row => row.CatalogKey).HasMaxLength(120);
            entity.Property(row => row.ContactCapabilitiesJson).HasColumnType("jsonb");
            entity.Property(row => row.CategoryKeysJson).HasColumnType("jsonb");
            entity.Property(row => row.DistrictKey).HasMaxLength(120);
            entity.Property(row => row.SourceRevision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.ListingId, row.SourceRevision });
        });

        modelBuilder.Entity<PromotionCommandRow>(entity =>
        {
            entity.ToTable("command_result", "operations", table =>
            {
                table.HasCheckConstraint(
                    "ck_promotion_command_request_digest",
                    "request_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_promotion_command_result_digest",
                    "result_digest ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(row => new { row.Scope, row.IdempotencyKey });
            entity.Property(row => row.Scope).HasMaxLength(150);
            entity.Property(row => row.IdempotencyKey).HasMaxLength(200);
            entity.Property(row => row.RequestDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.ResultKind).HasMaxLength(50);
            entity.Property(row => row.ResultJson).HasColumnType("jsonb");
            entity.Property(row => row.ResultDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.HasIndex(row => row.CreatedAtUtc);
        });

        modelBuilder.Entity<PromotionOutboxRow>(entity =>
        {
            entity.ToTable("outbox_message", "messaging", table =>
            {
                table.HasCheckConstraint(
                    "ck_promotion_outbox_payload_digest",
                    "payload_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_promotion_outbox_attempts", "delivery_attempts >= 0");
                table.HasCheckConstraint(
                    "ck_promotion_outbox_lease_shape",
                    "(lease_token IS NULL AND leased_by IS NULL AND lease_expires_at_utc IS NULL) OR (lease_token IS NOT NULL AND leased_by IS NOT NULL AND lease_expires_at_utc IS NOT NULL)");
                table.HasCheckConstraint(
                    "ck_promotion_outbox_terminal_shape",
                    "NOT (dispatched_at_utc IS NOT NULL AND dead_lettered_at_utc IS NOT NULL)");
            });
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.RoutingKey).HasMaxLength(256);
            entity.Property(row => row.ContractIdentity).HasMaxLength(256);
            entity.Property(row => row.PayloadJson).HasColumnType("text");
            entity.Property(row => row.PayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.LeasedBy).HasMaxLength(200);
            entity.Property(row => row.LastError).HasMaxLength(2000);
            entity.Property(row => row.DeadLetterReason).HasMaxLength(2000);
            entity.HasIndex(row => new
            {
                row.DispatchedAtUtc,
                row.DeadLetteredAtUtc,
                row.OccurredAtUtc,
            });
            entity.HasIndex(row => row.LeaseExpiresAtUtc);
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
