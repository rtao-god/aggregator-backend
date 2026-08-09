using Microsoft.EntityFrameworkCore;

namespace Aggregator.Ingestion.Infrastructure;

public sealed class IngestionDbContext(DbContextOptions<IngestionDbContext> options) : DbContext(options)
{
    internal DbSet<ImportBatchRow> Batches => Set<ImportBatchRow>();

    internal DbSet<ImportBatchManifestRow> Manifests => Set<ImportBatchManifestRow>();

    internal DbSet<ImportBatchSourcePolicyRow> SourcePolicies => Set<ImportBatchSourcePolicyRow>();

    internal DbSet<ImportBatchArtifactRow> Artifacts => Set<ImportBatchArtifactRow>();

    internal DbSet<IngestionCommandRow> Commands => Set<IngestionCommandRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureBatch(modelBuilder);
        ConfigureManifest(modelBuilder);
        ConfigureSourcePolicy(modelBuilder);
        ConfigureArtifact(modelBuilder);
        ConfigureCommand(modelBuilder);
    }

    private static void ConfigureBatch(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ImportBatchRow>();
        entity.ToTable("import_batch", "batches");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).HasColumnName("id");
        entity.Property(row => row.ProducerIdentity).HasColumnName("producer_identity").HasMaxLength(200);
        entity.Property(row => row.ProducerBuild).HasColumnName("producer_build").HasMaxLength(200);
        entity.Property(row => row.CollectorExportId).HasColumnName("collector_export_id");
        entity.Property(row => row.CollectorExportDigest).HasColumnName("collector_export_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.TargetSiteKey).HasColumnName("target_site_key").HasMaxLength(96);
        entity.Property(row => row.TargetCatalogKey).HasColumnName("target_catalog_key").HasMaxLength(96);
        entity.Property(row => row.TargetCatalogConfigurationRevisionId)
            .HasColumnName("target_catalog_configuration_revision_id");
        entity.Property(row => row.ExpectedItemCount).HasColumnName("expected_item_count");
        entity.Property(row => row.ManifestDigest).HasColumnName("manifest_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.ItemIndexDigest).HasColumnName("item_index_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.PayloadDigest).HasColumnName("payload_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.PayloadObjectKey).HasColumnName("payload_object_key").HasMaxLength(1024);
        entity.Property(row => row.PayloadObjectDigest).HasColumnName("payload_object_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.PayloadObjectSize).HasColumnName("payload_object_size");
        entity.Property(row => row.PayloadContentType).HasColumnName("payload_content_type").HasMaxLength(200);
        entity.Property(row => row.RegisteredAtUtc).HasColumnName("registered_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.LastChangedAtUtc).HasColumnName("last_changed_at_utc").HasColumnType("timestamp with time zone");
        entity.Property(row => row.State).HasColumnName("state");
        entity.Property(row => row.AggregateRevision).HasColumnName("aggregate_revision").IsConcurrencyToken();
        entity.Property(row => row.AcceptedItemCount).HasColumnName("accepted_item_count");
        entity.Property(row => row.ReviewRequiredItemCount).HasColumnName("review_required_item_count");
        entity.Property(row => row.RejectedItemCount).HasColumnName("rejected_item_count");
        entity.Property(row => row.FailureCode).HasColumnName("failure_code").HasMaxLength(200);
        entity.HasIndex(row => new { row.ProducerIdentity, row.CollectorExportId }).IsUnique();
        entity.HasIndex(row => new { row.TargetCatalogKey, row.State, row.RegisteredAtUtc });
        entity.ToTable(
            table =>
            {
                table.HasCheckConstraint("ck_import_batch_item_count", "expected_item_count BETWEEN 1 AND 100000");
                table.HasCheckConstraint("ck_import_batch_payload_size", "payload_object_size > 0");
                table.HasCheckConstraint("ck_import_batch_state", "state BETWEEN 1 AND 18");
                table.HasCheckConstraint("ck_import_batch_revision", "aggregate_revision > 0");
                table.HasCheckConstraint(
                    "ck_import_batch_decision_counts",
                    "accepted_item_count >= 0 AND review_required_item_count >= 0 AND rejected_item_count >= 0 AND accepted_item_count + review_required_item_count + rejected_item_count <= expected_item_count");
                table.HasCheckConstraint(
                    "ck_import_batch_time_order",
                    "last_changed_at_utc >= registered_at_utc");
            });
    }

    private static void ConfigureManifest(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ImportBatchManifestRow>();
        entity.ToTable("import_batch_manifest", "batches");
        entity.HasKey(row => row.BatchId);
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.ContractIdentity).HasColumnName("contract_identity").HasMaxLength(200);
        entity.Property(row => row.ContractRevision).HasColumnName("contract_revision");
        entity.Property(row => row.CanonicalDocument).HasColumnName("canonical_document").HasColumnType("bytea");
        entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.HasOne<ImportBatchRow>()
            .WithOne()
            .HasForeignKey<ImportBatchManifestRow>(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSourcePolicy(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ImportBatchSourcePolicyRow>();
        entity.ToTable("import_batch_source_policy", "batches");
        entity.HasKey(row => new { row.BatchId, row.SourceKey });
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.SourceKey).HasColumnName("source_key").HasMaxLength(96);
        entity.Property(row => row.PolicyDigest).HasColumnName("policy_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.UsagePolicy).HasColumnName("usage_policy");
        entity.HasOne<ImportBatchRow>()
            .WithMany()
            .HasForeignKey(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(
            table => table.HasCheckConstraint(
                "ck_import_batch_source_policy_usage",
                "usage_policy BETWEEN 1 AND 7"));
    }

    private static void ConfigureArtifact(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<ImportBatchArtifactRow>();
        entity.ToTable("import_batch_artifact", "batches");
        entity.HasKey(row => new { row.BatchId, row.Role, row.ObjectKey });
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.Role).HasColumnName("role");
        entity.Property(row => row.ObjectKey).HasColumnName("object_key").HasMaxLength(1024);
        entity.Property(row => row.ContentDigest).HasColumnName("content_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.Size).HasColumnName("size");
        entity.Property(row => row.ContentType).HasColumnName("content_type").HasMaxLength(200);
        entity.HasOne<ImportBatchRow>()
            .WithMany()
            .HasForeignKey(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(row => row.ObjectKey).IsUnique();
        entity.ToTable(
            table =>
            {
                table.HasCheckConstraint("ck_import_batch_artifact_role", "role BETWEEN 1 AND 2");
                table.HasCheckConstraint("ck_import_batch_artifact_size", "size > 0");
            });
    }

    private static void ConfigureCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<IngestionCommandRow>();
        entity.ToTable("command_idempotency", "operations");
        entity.HasKey(row => new { row.Scope, row.Key });
        entity.Property(row => row.Scope).HasColumnName("scope").HasMaxLength(150);
        entity.Property(row => row.Key).HasColumnName("key").HasMaxLength(200);
        entity.Property(row => row.RequestDigest).HasColumnName("request_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.BatchId).HasColumnName("batch_id");
        entity.Property(row => row.ResultDocument).HasColumnName("result_document").HasColumnType("bytea");
        entity.Property(row => row.ResultDigest).HasColumnName("result_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.CallerServiceIdentity).HasColumnName("caller_service_identity").HasMaxLength(200);
        entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
        entity.HasOne<ImportBatchRow>()
            .WithMany()
            .HasForeignKey(row => row.BatchId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(
            table =>
            {
                table.HasCheckConstraint(
                    "ck_command_idempotency_result_document",
                    "octet_length(result_document) > 0");
                table.HasCheckConstraint(
                    "ck_command_idempotency_result_digest",
                    "result_digest ~ '^[0-9a-f]{64}$'");
            });
    }
}
