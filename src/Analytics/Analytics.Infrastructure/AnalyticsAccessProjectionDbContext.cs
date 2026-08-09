using Microsoft.EntityFrameworkCore;

namespace Aggregator.Analytics.Infrastructure;

/// <summary>Persistence boundary for Catalog-owned listing access grants projected into Analytics.</summary>
public sealed class AnalyticsAccessProjectionDbContext(
    DbContextOptions<AnalyticsAccessProjectionDbContext> options) : DbContext(options)
{
    internal DbSet<AnalyticsListingAccessGrantProjectionRow> ListingAccessProjections =>
        Set<AnalyticsListingAccessGrantProjectionRow>();

    internal DbSet<AnalyticsListingAccessGrantInboxRow> ListingAccessInboxMessages =>
        Set<AnalyticsListingAccessGrantInboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<AnalyticsListingAccessGrantProjectionRow>(entity =>
        {
            entity.ToTable("listing_access_grant_projection", "access_projection", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_listing_access_grant_ids",
                    "grant_id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND actor_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint(
                    "ck_analytics_listing_access_grant_revision",
                    "source_aggregate_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_listing_access_grant_digests",
                    "source_payload_digest ~ '^[0-9a-f]{64}$' AND projection_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_listing_access_grant_interval",
                    "(expires_at_utc IS NULL OR expires_at_utc > granted_at_utc) AND changed_at_utc >= granted_at_utc");
                table.HasCheckConstraint(
                    "ck_analytics_listing_access_grant_revocation",
                    "(revoked_at_utc IS NULL AND source_aggregate_revision = 1) OR (revoked_at_utc IS NOT NULL AND revoked_at_utc >= granted_at_utc AND source_aggregate_revision >= 2 AND can_view_analytics = false)");
            });
            entity.HasKey(row => row.GrantId);
            entity.Property(row => row.SourceAggregateRevision).IsConcurrencyToken();
            entity.Property(row => row.SourcePayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.ProjectionDigest).HasMaxLength(64).IsFixedLength();
            entity.HasIndex(row => new
            {
                row.ActorId,
                row.ListingId,
                row.CanViewAnalytics,
                row.RevokedAtUtc,
                row.ExpiresAtUtc,
            });
        });

        modelBuilder.Entity<AnalyticsListingAccessGrantInboxRow>(entity =>
        {
            entity.ToTable("listing_access_grant_inbox", "messaging", table =>
            {
                table.HasCheckConstraint(
                    "ck_analytics_access_grant_inbox_ids",
                    "message_id <> '00000000-0000-0000-0000-000000000000'::uuid AND grant_id <> '00000000-0000-0000-0000-000000000000'::uuid AND listing_id <> '00000000-0000-0000-0000-000000000000'::uuid AND actor_id <> '00000000-0000-0000-0000-000000000000'::uuid");
                table.HasCheckConstraint(
                    "ck_analytics_access_grant_inbox_revision",
                    "source_aggregate_revision > 0");
                table.HasCheckConstraint(
                    "ck_analytics_access_grant_inbox_digests",
                    "payload_digest ~ '^[0-9a-f]{64}$' AND result_projection_digest ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint(
                    "ck_analytics_access_grant_inbox_disposition",
                    "disposition BETWEEN 1 AND 3");
                table.HasCheckConstraint(
                    "ck_analytics_access_grant_inbox_processing_time",
                    "processed_at_utc >= received_at_utc");
            });
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.RoutingKey).HasMaxLength(200);
            entity.Property(row => row.ContractIdentity).HasMaxLength(200);
            entity.Property(row => row.PayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.ResultProjectionDigest).HasMaxLength(64).IsFixedLength();
            entity.HasOne(row => row.Grant)
                .WithMany()
                .HasForeignKey(row => row.GrantId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(row => new
            {
                row.GrantId,
                row.SourceAggregateRevision,
                row.MessageId,
            });
        });
    }
}

internal sealed class AnalyticsListingAccessGrantProjectionRow
{
    public Guid GrantId { get; set; }

    public Guid ListingId { get; set; }

    public Guid ActorId { get; set; }

    public bool CanViewAnalytics { get; set; }

    public DateTimeOffset GrantedAtUtc { get; set; }

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public DateTimeOffset? RevokedAtUtc { get; set; }

    public long SourceAggregateRevision { get; set; }

    public required string SourcePayloadDigest { get; set; }

    public required string ProjectionDigest { get; set; }

    public DateTimeOffset ChangedAtUtc { get; set; }
}

internal sealed class AnalyticsListingAccessGrantInboxRow
{
    public Guid MessageId { get; set; }

    public Guid GrantId { get; set; }

    public AnalyticsListingAccessGrantProjectionRow Grant { get; set; } = null!;

    public Guid ListingId { get; set; }

    public Guid ActorId { get; set; }

    public required string RoutingKey { get; set; }

    public required string ContractIdentity { get; set; }

    public required string PayloadDigest { get; set; }

    public long SourceAggregateRevision { get; set; }

    public DateTimeOffset ReceivedAtUtc { get; set; }

    public required string CorrelationId { get; set; }

    public Guid? CausationId { get; set; }

    public int Disposition { get; set; }

    public required string ResultProjectionDigest { get; set; }

    public DateTimeOffset ProcessedAtUtc { get; set; }
}
