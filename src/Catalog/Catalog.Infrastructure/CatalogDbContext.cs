using Microsoft.EntityFrameworkCore;

namespace Aggregator.Catalog.Infrastructure;

/// <summary>Catalog-owned EF Core persistence boundary.</summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options)
    : DbContext(options)
{
    internal DbSet<CatalogConfigurationRevisionRow> ConfigurationRevisions => Set<CatalogConfigurationRevisionRow>();

    internal DbSet<CatalogConfigurationValidationResultRow> ConfigurationValidationResults => Set<CatalogConfigurationValidationResultRow>();

    internal DbSet<ActiveCatalogConfigurationRow> ActiveConfigurations => Set<ActiveCatalogConfigurationRow>();

    internal DbSet<CatalogListingRow> Listings => Set<CatalogListingRow>();

    internal DbSet<CatalogListingRevisionRow> ListingRevisions => Set<CatalogListingRevisionRow>();

    internal DbSet<CatalogProvenanceAssertionRow> ProvenanceAssertions => Set<CatalogProvenanceAssertionRow>();

    internal DbSet<CatalogLocalizedTextRow> LocalizedTexts => Set<CatalogLocalizedTextRow>();

    internal DbSet<CatalogCategoryAssignmentRow> CategoryAssignments => Set<CatalogCategoryAssignmentRow>();

    internal DbSet<CatalogAttributeValueRow> AttributeValues => Set<CatalogAttributeValueRow>();

    internal DbSet<CatalogGeographyRow> Geographies => Set<CatalogGeographyRow>();

    internal DbSet<CatalogContactRow> Contacts => Set<CatalogContactRow>();

    internal DbSet<CatalogMediaRow> Media => Set<CatalogMediaRow>();

    internal DbSet<CatalogEditorialDecisionRow> EditorialDecisions => Set<CatalogEditorialDecisionRow>();

    internal DbSet<CatalogPublicationRow> Publications => Set<CatalogPublicationRow>();

    internal DbSet<CatalogPublicationEntryRow> PublicationEntries => Set<CatalogPublicationEntryRow>();

    internal DbSet<CurrentCatalogPublicationRow> CurrentPublications => Set<CurrentCatalogPublicationRow>();

    internal DbSet<CatalogClaimRow> Claims => Set<CatalogClaimRow>();

    internal DbSet<CatalogListingAccessGrantRow> ListingAccessGrants => Set<CatalogListingAccessGrantRow>();

    internal DbSet<CatalogListingAccessScopeRow> ListingAccessScopes => Set<CatalogListingAccessScopeRow>();

    internal DbSet<CatalogListingDisputeRow> ListingDisputes => Set<CatalogListingDisputeRow>();

    internal DbSet<CatalogPublicationOperationRow> PublicationOperations => Set<CatalogPublicationOperationRow>();

    internal DbSet<CatalogOutboxRow> OutboxMessages => Set<CatalogOutboxRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("catalog");

        modelBuilder.Entity<CatalogConfigurationRevisionRow>(entity =>
        {
            entity.ToTable("configuration_revision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.Property(row => row.CanonicalDocument).HasColumnType("bytea");
            entity.HasIndex(row => new { row.CatalogKey, row.ContentDigest }).IsUnique();
        });

        modelBuilder.Entity<CatalogConfigurationValidationResultRow>(entity =>
        {
            entity.ToTable("configuration_validation_result");
            entity.HasKey(row => row.ConfigurationRevisionId);
            entity.Property(row => row.ValidatorIdentity).HasMaxLength(200);
            entity.Property(row => row.ValidatorRevision).HasMaxLength(100);
            entity.Property(row => row.SemanticFingerprint).HasMaxLength(64);
            entity.HasOne<CatalogConfigurationRevisionRow>()
                .WithOne()
                .HasForeignKey<CatalogConfigurationValidationResultRow>(row => row.ConfigurationRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ActiveCatalogConfigurationRow>(entity =>
        {
            entity.ToTable("active_configuration");
            entity.HasKey(row => row.CatalogKey);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.HasIndex(row => row.ConfigurationRevisionId);
        });

        modelBuilder.Entity<CatalogListingRow>(entity =>
        {
            entity.ToTable("listing");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.ArchiveReason).HasMaxLength(2048);
            entity.HasIndex(row => new { row.CatalogKey, row.SubjectKind, row.SubjectId }).IsUnique();
        });

        modelBuilder.Entity<CatalogListingRevisionRow>(entity =>
        {
            entity.ToTable("listing_revision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.HasIndex(row => new { row.ListingId, row.RevisionNumber }).IsUnique();
        });

        modelBuilder.Entity<CatalogProvenanceAssertionRow>(entity =>
        {
            entity.ToTable("provenance_assertion");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.SourceReference).HasMaxLength(2048);
            entity.Property(row => row.EvidenceDigest).HasMaxLength(64);
            entity.HasIndex(row => row.ListingRevisionId);
        });

        modelBuilder.Entity<CatalogLocalizedTextRow>(entity =>
        {
            entity.ToTable("localized_text");
            entity.HasKey(row => new { row.ListingRevisionId, row.FieldKind, row.Locale });
            entity.Property(row => row.Locale).HasMaxLength(35);
            entity.Property(row => row.Value).HasMaxLength(4096);
            entity.Property(row => row.MissingReason).HasMaxLength(512);
        });

        modelBuilder.Entity<CatalogCategoryAssignmentRow>(entity =>
        {
            entity.ToTable("category_assignment");
            entity.HasKey(row => new { row.ListingRevisionId, row.CategoryKey });
            entity.Property(row => row.CategoryKey).HasMaxLength(96);
        });

        modelBuilder.Entity<CatalogAttributeValueRow>(entity =>
        {
            entity.ToTable("attribute_value");
            entity.HasKey(row => new { row.ListingRevisionId, row.AttributeKey });
            entity.Property(row => row.AttributeKey).HasMaxLength(96);
            entity.Property(row => row.StringValue).HasMaxLength(4096);
            entity.Property(row => row.DateValue).HasMaxLength(10);
            entity.Property(row => row.EnumValue).HasMaxLength(200);
            entity.Property(row => row.CurrencyCode).HasMaxLength(3);
            entity.Property(row => row.MissingReason).HasMaxLength(512);
        });

        modelBuilder.Entity<CatalogGeographyRow>(entity =>
        {
            entity.ToTable("geography_value");
            entity.HasKey(row => row.ListingRevisionId);
            entity.Property(row => row.AddressText).HasMaxLength(1000);
            entity.Property(row => row.DistrictKey).HasMaxLength(96);
        });

        modelBuilder.Entity<CatalogContactRow>(entity =>
        {
            entity.ToTable("contact_value");
            entity.HasKey(row => new { row.ListingRevisionId, row.ContactId });
            entity.Property(row => row.Target).HasMaxLength(2048);
            entity.Property(row => row.Label).HasMaxLength(200);
        });

        modelBuilder.Entity<CatalogMediaRow>(entity =>
        {
            entity.ToTable("listing_media");
            entity.HasKey(row => new
            {
                row.ListingRevisionId,
                row.MediaId,
                row.MediaAggregateRevision,
                row.VariantId,
            });
            entity.Property(row => row.ObjectUri).HasMaxLength(2048);
            entity.Property(row => row.ContentType).HasMaxLength(200);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.Property(row => row.Caption).HasMaxLength(1000);
            entity.HasIndex(row => new
            {
                row.MediaId,
                row.MediaAggregateRevision,
                row.VariantId,
            });
        });

        modelBuilder.Entity<CatalogEditorialDecisionRow>(entity =>
        {
            entity.ToTable("editorial_decision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Reason).HasMaxLength(2048);
            entity.HasIndex(row => new { row.ListingId, row.DecidedAtUtc });
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
            entity.ToTable("current_catalog_publication");
            entity.HasKey(row => row.CatalogKey);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.HasIndex(row => row.PublicationId);
        });

        modelBuilder.Entity<CatalogClaimRow>(entity =>
        {
            entity.ToTable("listing_claim");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.EvidenceReference).HasMaxLength(2048);
            entity.Property(row => row.EvidenceDigest).HasMaxLength(64);
            entity.Property(row => row.DecisionReason).HasMaxLength(2048);
            entity.HasIndex(row => new { row.ListingId, row.State });
        });

        modelBuilder.Entity<CatalogListingAccessGrantRow>(entity =>
        {
            entity.ToTable("listing_access_grant");
            entity.HasKey(row => row.Id);
            entity.HasIndex(row => new { row.ListingId, row.ActorId }).IsUnique();
        });

        modelBuilder.Entity<CatalogListingAccessScopeRow>(entity =>
        {
            entity.ToTable("listing_access_scope");
            entity.HasKey(row => new { row.GrantId, row.Scope });
        });

        modelBuilder.Entity<CatalogListingDisputeRow>(entity =>
        {
            entity.ToTable("listing_dispute");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.OpenReason).HasMaxLength(2000);
            entity.Property(row => row.ResolutionReason).HasMaxLength(2000);
            entity.Property(row => row.AggregateRevision).IsConcurrencyToken();
            entity.HasIndex(row => row.ListingId);
            entity.HasIndex(row => row.ListingId)
                .IsUnique()
                .HasFilter("state = 1");
            entity.HasOne<CatalogListingRow>()
                .WithMany()
                .HasForeignKey(row => row.ListingId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CatalogPublicationOperationRow>(entity =>
        {
            entity.ToTable("publication_operation");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.IdempotencyKey).HasMaxLength(128);
            entity.Property(row => row.RequestDocument).HasColumnType("bytea");
            entity.Property(row => row.RequestDigest).HasMaxLength(64);
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.LeasedBy).HasMaxLength(200);
            entity.Property(row => row.FailureOwner).HasMaxLength(200);
            entity.Property(row => row.FailureCode).HasMaxLength(200);
            entity.Property(row => row.FailureDetail).HasMaxLength(4000);
            entity.Property(row => row.FailureRequiredAction).HasMaxLength(2000);
            entity.HasIndex(row => row.PublicationId).IsUnique();
            entity.HasIndex(row => new { row.CatalogKey, row.PublicationSequence }).IsUnique();
            entity.HasIndex(row => new { row.CatalogKey, row.ActorId, row.IdempotencyKey }).IsUnique();
            entity.HasIndex(row => new { row.State, row.NextAttemptAtUtc, row.CreatedAtUtc });
            entity.HasIndex(row => row.LeaseExpiresAtUtc);
        });

        modelBuilder.Entity<CatalogOutboxRow>(entity =>
        {
            entity.ToTable("outbox_message", "messaging");
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.RoutingKey).HasMaxLength(200);
            entity.Property(row => row.ContractIdentity).HasMaxLength(200);
            entity.Property(row => row.PayloadJson).HasColumnType("text");
            entity.Property(row => row.PayloadDigest).HasMaxLength(64);
            entity.Property(row => row.CorrelationId).HasMaxLength(128);
            entity.Property(row => row.LeasedBy).HasMaxLength(200);
            entity.Property(row => row.LastError).HasMaxLength(4000);
            entity.Property(row => row.DeadLetterReason).HasMaxLength(4000);
            entity.HasIndex(row => new { row.DispatchedAtUtc, row.DeadLetteredAtUtc, row.OccurredAtUtc });
            entity.HasIndex(row => row.LeaseExpiresAtUtc);
        });
    }
}
