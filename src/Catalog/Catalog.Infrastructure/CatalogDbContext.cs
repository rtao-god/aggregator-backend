using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    internal DbSet<CatalogConfigurationRevisionRow> ConfigurationRevisions => Set<CatalogConfigurationRevisionRow>();

    internal DbSet<ActiveCatalogConfigurationRow> ActiveConfigurations => Set<ActiveCatalogConfigurationRow>();

    internal DbSet<CatalogListingRow> Listings => Set<CatalogListingRow>();

    internal DbSet<CatalogListingRevisionRow> ListingRevisions => Set<CatalogListingRevisionRow>();

    internal DbSet<CatalogProvenanceAssertionRow> ProvenanceAssertions => Set<CatalogProvenanceAssertionRow>();

    internal DbSet<CatalogLocalizedTextRow> LocalizedTexts => Set<CatalogLocalizedTextRow>();

    internal DbSet<CatalogCategoryAssignmentRow> CategoryAssignments => Set<CatalogCategoryAssignmentRow>();

    internal DbSet<CatalogAttributeValueRow> AttributeValues => Set<CatalogAttributeValueRow>();

    internal DbSet<CatalogGeographyRow> Geographies => Set<CatalogGeographyRow>();

    internal DbSet<CatalogContactRow> Contacts => Set<CatalogContactRow>();

    internal DbSet<CatalogMediaRow> ListingMedia => Set<CatalogMediaRow>();

    internal DbSet<CatalogEditorialDecisionRow> EditorialDecisions => Set<CatalogEditorialDecisionRow>();

    internal DbSet<CatalogPublicationRow> Publications => Set<CatalogPublicationRow>();

    internal DbSet<CatalogPublicationEntryRow> PublicationEntries => Set<CatalogPublicationEntryRow>();

    internal DbSet<CurrentCatalogPublicationRow> CurrentPublications => Set<CurrentCatalogPublicationRow>();

    internal DbSet<CatalogListingClaimRow> ListingClaims => Set<CatalogListingClaimRow>();

    internal DbSet<CatalogListingAccessGrantRow> ListingAccessGrants => Set<CatalogListingAccessGrantRow>();

    internal DbSet<CatalogListingAccessScopeRow> ListingAccessScopes => Set<CatalogListingAccessScopeRow>();

    internal DbSet<CatalogPublicationOperationRow> PublicationOperations => Set<CatalogPublicationOperationRow>();

    internal DbSet<CatalogOutboxRow> OutboxMessages => Set<CatalogOutboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema("catalog");

        modelBuilder.Entity<CatalogConfigurationRevisionRow>(entity =>
        {
            entity.ToTable("configuration_revision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.SiteKey).HasMaxLength(96);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.Property(row => row.CanonicalDocument).HasColumnType("bytea");
            entity.HasIndex(row => new { row.CatalogKey, row.ContentDigest }).IsUnique();
        });

        modelBuilder.Entity<ActiveCatalogConfigurationRow>(entity =>
        {
            entity.ToTable("active_configuration");
            entity.HasKey(row => row.CatalogKey);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
        });

        modelBuilder.Entity<CatalogListingRow>(entity =>
        {
            entity.ToTable("listing");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasIndex(row => new { row.CatalogKey, row.SubjectKind, row.SubjectId }).IsUnique();
            entity.HasOne<CatalogConfigurationRevisionRow>()
                .WithMany()
                .HasForeignKey(row => row.CurrentDraftRevisionId)
                .IsRequired(false)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CatalogListingRevisionRow>(entity =>
        {
            entity.ToTable("listing_revision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.HasIndex(row => new { row.ListingId, row.RevisionNumber }).IsUnique();
            entity.HasOne<CatalogListingRow>()
                .WithMany()
                .HasForeignKey(row => row.ListingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CatalogProvenanceAssertionRow>(entity =>
        {
            entity.ToTable("provenance_assertion");
            entity.HasKey(row => new { row.ListingRevisionId, row.AssertionId });
            entity.Property(row => row.SourceReference).HasMaxLength(2048);
            entity.Property(row => row.EvidenceDigest).HasMaxLength(64);
        });

        modelBuilder.Entity<CatalogLocalizedTextRow>(entity =>
        {
            entity.ToTable("localized_text");
            entity.HasKey(row => new { row.ListingRevisionId, row.FieldKind, row.Locale });
            entity.Property(row => row.FieldKind).HasMaxLength(32);
            entity.Property(row => row.Locale).HasMaxLength(16);
            entity.Property(row => row.TextValue).HasMaxLength(4000);
        });

        modelBuilder.Entity<CatalogCategoryAssignmentRow>(entity =>
        {
            entity.ToTable("category_assignment");
            entity.HasKey(row => new { row.ListingRevisionId, row.CategoryKey });
            entity.Property(row => row.CategoryKey).HasMaxLength(128);
        });

        modelBuilder.Entity<CatalogAttributeValueRow>(entity =>
        {
            entity.ToTable("attribute_value");
            entity.HasKey(row => new { row.ListingRevisionId, row.AttributeKey });
            entity.Property(row => row.AttributeKey).HasMaxLength(128);
            entity.Property(row => row.DecimalValue).HasPrecision(19, 6);
            entity.Property(row => row.TextValue).HasMaxLength(4000);
            entity.Property(row => row.TextSetValue).HasColumnType("text[]");
        });

        modelBuilder.Entity<CatalogGeographyRow>(entity =>
        {
            entity.ToTable("geography_value");
            entity.HasKey(row => row.ListingRevisionId);
            entity.Property(row => row.Latitude).HasPrecision(9, 6);
            entity.Property(row => row.Longitude).HasPrecision(9, 6);
            entity.Property(row => row.DistrictKey).HasMaxLength(128);
        });

        modelBuilder.Entity<CatalogContactRow>(entity =>
        {
            entity.ToTable("contact_value");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.ListingRevisionId, row.Kind, row.Target }).IsUnique();
            entity.Property(row => row.Target).HasMaxLength(2048);
            entity.Property(row => row.Label).HasMaxLength(512);
        });

        modelBuilder.Entity<CatalogMediaRow>(entity =>
        {
            entity.ToTable("listing_media");
            entity.HasKey(row => new { row.ListingRevisionId, row.MediaId });
            entity.HasIndex(row => new { row.ListingRevisionId, row.DisplayOrder }).IsUnique();
            entity.HasIndex(row => new { row.MediaId, row.MediaAggregateRevision });
            entity.Property(row => row.ObjectUri).HasMaxLength(2048);
            entity.Property(row => row.ContentType).HasMaxLength(256);
            entity.Property(row => row.ContentDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.Caption).HasMaxLength(1024);
        });

        modelBuilder.Entity<CatalogEditorialDecisionRow>(entity =>
        {
            entity.ToTable("editorial_decision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Reason).HasMaxLength(4096);
            entity.HasIndex(row => new { row.ListingId, row.RevisionId, row.Kind });
        });

        modelBuilder.Entity<CatalogPublicationRow>(entity =>
        {
            entity.ToTable("publication");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.ArtifactKey).HasMaxLength(1024);
            entity.Property(row => row.ArtifactDigest).HasMaxLength(64);
            entity.HasIndex(row => new { row.CatalogKey, row.Sequence }).IsUnique();
        });

        modelBuilder.Entity<CatalogPublicationEntryRow>(entity =>
        {
            entity.ToTable("publication_entry");
            entity.HasKey(row => new { row.PublicationId, row.ListingId });
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.HasIndex(row => row.ListingRevisionId);
        });

        modelBuilder.Entity<CurrentCatalogPublicationRow>(entity =>
        {
            entity.ToTable("current_publication");
            entity.HasKey(row => row.CatalogKey);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
        });

        modelBuilder.Entity<CatalogListingClaimRow>(entity =>
        {
            entity.ToTable("listing_claim");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.EvidenceReference).HasMaxLength(2048);
            entity.Property(row => row.EvidenceDigest).HasMaxLength(64);
            entity.Property(row => row.DecisionReason).HasMaxLength(4096);
            entity.HasIndex(row => new { row.ListingId, row.State });
        });

        modelBuilder.Entity<CatalogListingAccessGrantRow>(entity =>
        {
            entity.ToTable("listing_access_grant");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.RevocationReason).HasMaxLength(4096);
            entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
            entity.HasIndex(row => new { row.ListingId, row.ActorId });
            entity.HasIndex(row => row.ClaimId).IsUnique();
        });

        modelBuilder.Entity<CatalogListingAccessScopeRow>(entity =>
        {
            entity.ToTable("listing_access_scope");
            entity.HasKey(row => new { row.GrantId, row.Scope });
        });

        modelBuilder.Entity<CatalogPublicationOperationRow>(entity =>
        {
            entity.ToTable("publication_operation", "operations");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.IdempotencyKey).HasMaxLength(200);
            entity.Property(row => row.RequestJson).HasColumnType("text");
            entity.Property(row => row.RequestDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.FailureOwner).HasMaxLength(200);
            entity.Property(row => row.FailureCode).HasMaxLength(200);
            entity.Property(row => row.FailureDetail).HasMaxLength(4000);
            entity.Property(row => row.FailureRequiredAction).HasMaxLength(2000);
            entity.Property(row => row.State).IsConcurrencyToken();
            entity.HasIndex(row => new { row.CatalogKey, row.ActorId, row.IdempotencyKey }).IsUnique();
            entity.HasIndex(row => new { row.State, row.NextAttemptAtUtc, row.CreatedAtUtc });
        });

        modelBuilder.Entity<CatalogOutboxRow>(entity =>
        {
            entity.ToTable("outbox_message", "messaging");
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.MessageId).HasColumnName("id");
            entity.Property(row => row.RoutingKey).HasColumnName("event_type").HasMaxLength(256);
            entity.Property(row => row.ContractIdentity).HasColumnName("contract_identity").HasMaxLength(256);
            entity.Property(row => row.PayloadJson).HasColumnName("payload").HasColumnType("text");
            entity.Property(row => row.PayloadDigest).HasMaxLength(64).IsFixedLength();
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.LeasedBy).HasMaxLength(200);
            entity.Property(row => row.LastError).HasMaxLength(2000);
            entity.Property(row => row.DeadLetterReason).HasMaxLength(2000);
            entity.HasIndex(row => new { row.DispatchedAtUtc, row.DeadLetteredAtUtc, row.LeaseExpiresAtUtc, row.OccurredAtUtc });
        });
    }

    internal static string RequireUtf8(byte[] value, string columnName)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        try
        {
            return new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(value);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidOperationException(
                $"Catalog column '{columnName}' contains invalid UTF-8 bytes.",
                exception);
        }
    }
}
