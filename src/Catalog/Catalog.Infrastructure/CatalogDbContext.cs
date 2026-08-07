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

    internal DbSet<CatalogMediaRow> Media => Set<CatalogMediaRow>();

    internal DbSet<CatalogEditorialDecisionRow> EditorialDecisions => Set<CatalogEditorialDecisionRow>();

    internal DbSet<CatalogPublicationRow> Publications => Set<CatalogPublicationRow>();

    internal DbSet<CatalogPublicationEntryRow> PublicationEntries => Set<CatalogPublicationEntryRow>();

    internal DbSet<CurrentCatalogPublicationRow> CurrentPublications => Set<CurrentCatalogPublicationRow>();

    internal DbSet<CatalogListingClaimRow> ListingClaims => Set<CatalogListingClaimRow>();

    internal DbSet<CatalogListingAccessGrantRow> ListingAccessGrants => Set<CatalogListingAccessGrantRow>();

    internal DbSet<CatalogListingAccessScopeRow> ListingAccessScopes => Set<CatalogListingAccessScopeRow>();

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
        });

        modelBuilder.Entity<CatalogListingRow>(entity =>
        {
            entity.ToTable("listing");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.Version).IsConcurrencyToken();
            entity.HasIndex(row => new { row.CatalogKey, row.SubjectId }).IsUnique();
            entity.HasIndex(row => new { row.CatalogKey, row.State });
        });

        modelBuilder.Entity<CatalogListingRevisionRow>(entity =>
        {
            entity.ToTable("listing_revision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.HasIndex(row => new { row.ListingId, row.RevisionNumber }).IsUnique();
            entity.HasIndex(row => row.ConfigurationRevisionId);
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
            entity.Property(row => row.FieldKind).HasMaxLength(24);
            entity.Property(row => row.Locale).HasMaxLength(32);
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
            entity.Property(row => row.DecimalValue).HasPrecision(24, 8);
            entity.Property(row => row.TextSetValue).HasColumnType("text[]");
        });

        modelBuilder.Entity<CatalogGeographyRow>(entity =>
        {
            entity.ToTable("geography");
            entity.HasKey(row => row.ListingRevisionId);
            entity.Property(row => row.Latitude).HasPrecision(9, 6);
            entity.Property(row => row.Longitude).HasPrecision(9, 6);
            entity.Property(row => row.DistrictKey).HasMaxLength(96);
        });

        modelBuilder.Entity<CatalogContactRow>(entity =>
        {
            entity.ToTable("contact");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Target).HasMaxLength(2048);
            entity.Property(row => row.Label).HasMaxLength(256);
            entity.HasIndex(row => new { row.ListingRevisionId, row.Kind, row.Target }).IsUnique();
        });

        modelBuilder.Entity<CatalogMediaRow>(entity =>
        {
            entity.ToTable("media");
            entity.HasKey(row => new { row.ListingRevisionId, row.MediaId });
            entity.Property(row => row.ObjectUri).HasMaxLength(256);
            entity.Property(row => row.ContentType).HasMaxLength(128);
            entity.Property(row => row.ContentDigest).HasMaxLength(64);
            entity.Property(row => row.Caption).HasMaxLength(500);
            entity.HasIndex(row => new { row.ListingRevisionId, row.VariantId }).IsUnique();
            entity.HasIndex(row => new { row.ListingRevisionId, row.DisplayOrder }).IsUnique();
        });

        modelBuilder.Entity<CatalogEditorialDecisionRow>(entity =>
        {
            entity.ToTable("editorial_decision");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.Reason).HasMaxLength(4096);
            entity.HasIndex(row => new { row.ListingId, row.RevisionId });
        });

        modelBuilder.Entity<CatalogPublicationRow>(entity =>
        {
            entity.ToTable("publication");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.CatalogKey).HasMaxLength(96);
            entity.Property(row => row.ArtifactKey).HasMaxLength(1024);
            entity.Property(row => row.ArtifactDigest).HasMaxLength(64);
            entity.HasIndex(row => new { row.CatalogKey, row.Sequence }).IsUnique();
            entity.HasIndex(row => new { row.CatalogKey, row.ArtifactDigest }).IsUnique();
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
            entity.HasIndex(row => new { row.ListingId, row.ClaimantActorId, row.State });
        });

        modelBuilder.Entity<CatalogListingAccessGrantRow>(entity =>
        {
            entity.ToTable("listing_access_grant");
            entity.HasKey(row => row.Id);
            entity.Property(row => row.RevocationReason).HasMaxLength(4096);
            entity.HasIndex(row => new { row.ListingId, row.ActorId });
            entity.HasIndex(row => row.ClaimId).IsUnique();
        });

        modelBuilder.Entity<CatalogListingAccessScopeRow>(entity =>
        {
            entity.ToTable("listing_access_scope");
            entity.HasKey(row => new { row.GrantId, row.Scope });
        });

        modelBuilder.Entity<CatalogOutboxRow>(entity =>
        {
            entity.ToTable("outbox_message");
            entity.HasKey(row => row.MessageId);
            entity.Property(row => row.RoutingKey).HasMaxLength(256);
            entity.Property(row => row.ContractIdentity).HasMaxLength(256);
            entity.Property(row => row.PayloadJson).HasColumnType("text");
            entity.Property(row => row.PayloadDigest).HasMaxLength(64);
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
