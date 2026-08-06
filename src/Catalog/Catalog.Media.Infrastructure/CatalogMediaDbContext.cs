using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.CatalogMedia.Infrastructure;

public sealed class CatalogMediaDbContext(DbContextOptions<CatalogMediaDbContext> options) : DbContext(options)
{
    internal DbSet<CatalogMediaAssetRow> Assets => Set<CatalogMediaAssetRow>();
    internal DbSet<CatalogMediaVariantRow> Variants => Set<CatalogMediaVariantRow>();
    internal DbSet<CatalogMediaCommandRow> Commands => Set<CatalogMediaCommandRow>();
    internal DbSet<CatalogMediaProcessingWorkRow> ProcessingWork => Set<CatalogMediaProcessingWorkRow>();
    internal DbSet<CatalogMediaOutboxRow> OutboxMessages => Set<CatalogMediaOutboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.Entity<CatalogMediaAssetRow>(entity =>
        {
            entity.ToTable("asset", "media");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(120);
            entity.Property(row => row.QuarantineObjectKey).HasMaxLength(1024);
            entity.Property(row => row.ExpectedContentType).HasMaxLength(128);
            entity.Property(row => row.ExpectedContentDigest).HasMaxLength(64);
            entity.Property(row => row.RightsReference).HasMaxLength(4000);
            entity.Property(row => row.FailureCode).HasMaxLength(120);
            entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
            entity.HasIndex(row => row.QuarantineObjectKey).IsUnique();
            entity.HasIndex(row => new { row.CatalogKey, row.State, row.RegisteredAtUtc });
        });
        modelBuilder.Entity<CatalogMediaVariantRow>(entity =>
        {
            entity.ToTable("variant", "media");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ObjectKey).HasMaxLength(1024);
            entity.Property(row => row.ContentType).HasMaxLength(128);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.HasIndex(row => new { row.AssetId, row.Kind }).IsUnique();
            entity.HasIndex(row => row.ObjectKey).IsUnique();
            entity.HasOne<CatalogMediaAssetRow>()
                .WithMany()
                .HasForeignKey(row => row.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CatalogMediaCommandRow>(entity =>
        {
            entity.ToTable("command_result", "operations");
            entity.HasKey(row => new { row.Scope, row.IdempotencyKey });
            entity.Property(row => row.Scope).HasMaxLength(180);
            entity.Property(row => row.IdempotencyKey).HasMaxLength(200);
            entity.Property(row => row.RequestDigest).HasMaxLength(64);
            entity.Property(row => row.ResultDocument).HasColumnType("bytea");
            entity.Property(row => row.ResultDigest).HasMaxLength(64);
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.HasIndex(row => row.AssetId);
            entity.HasOne<CatalogMediaAssetRow>()
                .WithMany()
                .HasForeignKey(row => row.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CatalogMediaProcessingWorkRow>(entity =>
        {
            entity.ToTable("processing_work", "operations");
            entity.HasKey(row => row.AssetId);
            entity.Property(row => row.LeasedBy).HasMaxLength(200);
            entity.Property(row => row.LastError).HasMaxLength(4000);
            entity.HasIndex(row => row.LeaseExpiresAtUtc);
            entity.HasOne<CatalogMediaAssetRow>()
                .WithOne()
                .HasForeignKey<CatalogMediaProcessingWorkRow>(row => row.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<CatalogMediaOutboxRow>(entity =>
        {
            entity.ToTable("outbox_message", "media_messaging");
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.RoutingKey).HasMaxLength(256);
            entity.Property(row => row.ContractIdentity).HasMaxLength(256);
            entity.Property(row => row.PayloadJson).HasColumnType("text");
            entity.Property(row => row.PayloadDigest).HasMaxLength(64);
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.LeasedBy).HasMaxLength(200);
            entity.Property(row => row.LastError).HasMaxLength(2000);
            entity.Property(row => row.DeadLetterReason).HasMaxLength(2000);
            entity.HasIndex(row => new { row.DispatchedAtUtc, row.DeadLetteredAtUtc, row.OccurredAtUtc });
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
            if (char.IsUpper(character) && index > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
