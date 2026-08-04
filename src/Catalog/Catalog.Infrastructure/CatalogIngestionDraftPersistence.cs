using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aggregator.Catalog.Infrastructure;

public sealed class CatalogIngestionDbContext(
    DbContextOptions<CatalogIngestionDbContext> options) : DbContext(options)
{
    internal DbSet<CatalogIngestionTargetRow> Targets => Set<CatalogIngestionTargetRow>();
    internal DbSet<CatalogIngestionDraftRow> Drafts => Set<CatalogIngestionDraftRow>();
    internal DbSet<CatalogIngestionCommandRow> Commands => Set<CatalogIngestionCommandRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureTarget(modelBuilder);
        ConfigureDraft(modelBuilder);
        ConfigureCommand(modelBuilder);
    }

    private static void ConfigureTarget(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CatalogIngestionTargetRow>();
        entity.ToTable("catalog_target", "catalog_ingestion");
        entity.HasKey(row => new { row.SiteKey, row.CatalogKey });
        entity.Property(row => row.SiteKey).HasColumnName("site_key").HasMaxLength(96);
        entity.Property(row => row.CatalogKey).HasColumnName("catalog_key").HasMaxLength(96);
        entity.Property(row => row.ActiveConfigurationRevisionId)
            .HasColumnName("active_configuration_revision_id");
        entity.Property(row => row.ProjectionRevision)
            .HasColumnName("projection_revision")
            .IsConcurrencyToken();
        entity.Property(row => row.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");
    }

    private static void ConfigureDraft(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CatalogIngestionDraftRow>();
        entity.ToTable("draft_proposal", "catalog_ingestion");
        entity.HasKey(row => row.ListingId);
        entity.Property(row => row.ListingId).HasColumnName("listing_id");
        entity.Property(row => row.ListingRevisionId).HasColumnName("listing_revision_id");
        entity.Property(row => row.IngestionBatchId).HasColumnName("ingestion_batch_id");
        entity.Property(row => row.IngestionItemKey).HasColumnName("ingestion_item_key").HasMaxLength(200);
        entity.Property(row => row.SiteKey).HasColumnName("site_key").HasMaxLength(96);
        entity.Property(row => row.CatalogKey).HasColumnName("catalog_key").HasMaxLength(96);
        entity.Property(row => row.CatalogConfigurationRevisionId)
            .HasColumnName("catalog_configuration_revision_id");
        entity.Property(row => row.EntityKind).HasColumnName("entity_kind").HasMaxLength(32);
        entity.Property(row => row.SubjectNaturalKey).HasColumnName("subject_natural_key").HasMaxLength(300);
        entity.Property(row => row.FieldsDocument).HasColumnName("fields_document").HasColumnType("bytea");
        entity.Property(row => row.FieldsDigest).HasColumnName("fields_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.AggregateRevision)
            .HasColumnName("aggregate_revision")
            .IsConcurrencyToken();
        entity.Property(row => row.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.Property(row => row.LastChangedAtUtc)
            .HasColumnName("last_changed_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.HasIndex(row => new
        {
            row.CatalogKey,
            row.EntityKind,
            row.SubjectNaturalKey,
        }).IsUnique();
        entity.HasIndex(row => new { row.IngestionBatchId, row.IngestionItemKey }).IsUnique();
    }

    private static void ConfigureCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<CatalogIngestionCommandRow>();
        entity.ToTable("command_result", "catalog_ingestion");
        entity.HasKey(row => row.CommandId);
        entity.Property(row => row.CommandId).HasColumnName("command_id");
        entity.Property(row => row.CommandDigest).HasColumnName("command_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.IngestionBatchId).HasColumnName("ingestion_batch_id");
        entity.Property(row => row.IngestionItemKey).HasColumnName("ingestion_item_key").HasMaxLength(200);
        entity.Property(row => row.ResultDocument).HasColumnName("result_document").HasColumnType("bytea");
        entity.Property(row => row.ResultDigest).HasColumnName("result_digest").HasMaxLength(64).IsFixedLength();
        entity.Property(row => row.CallerIdentity).HasColumnName("caller_identity").HasMaxLength(200);
        entity.Property(row => row.CreatedAtUtc).HasColumnName("created_at_utc").HasColumnType("timestamp with time zone");
    }
}

public sealed class EfCatalogIngestionDraftStore(CatalogIngestionDbContext dbContext)
    : ICatalogIngestionDraftStore
{
    public async Task<CatalogIngestionCommandOutcome> UpsertAsync(
        CatalogIngestionUpsertDraftCommand command,
        string callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var replay = await ReadReplayAsync(command, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }

        var target = await dbContext.Targets
            .AsNoTracking()
            .SingleOrDefaultAsync(row =>
                row.SiteKey == command.SiteKey &&
                row.CatalogKey == command.CatalogKey,
                cancellationToken)
            ?? throw DraftFailure(
                "CATALOG_INGESTION_TARGET_MISSING",
                409,
                "Catalog cannot prove the exact Site/Catalog ingestion target.",
                "Activate the Catalog configuration and wait for its local ingestion projection.");
        if (target.ActiveConfigurationRevisionId != command.ExpectedCatalogConfigurationRevisionId)
        {
            throw new CatalogIngestionDraftException(
                "Catalog.Configuration",
                "CATALOG_INGESTION_CONFIGURATION_STALE",
                409,
                "The command targets a Catalog configuration revision that is no longer active.",
                "Regenerate the Ingestion package against the exact active Catalog configuration.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["siteKey"] = command.SiteKey,
                    ["catalogKey"] = command.CatalogKey,
                    ["expectedConfigurationRevisionId"] = command.ExpectedCatalogConfigurationRevisionId,
                    ["activeConfigurationRevisionId"] = target.ActiveConfigurationRevisionId,
                });
        }

        var fieldsDocument = CatalogIngestionDocument.Serialize(command.Fields);
        var fieldsDigest = CatalogIngestionDocument.ComputeDigest(fieldsDocument);
        var draft = await dbContext.Drafts.SingleOrDefaultAsync(row =>
            row.CatalogKey == command.CatalogKey &&
            row.EntityKind == command.EntityKind &&
            row.SubjectNaturalKey == command.SubjectNaturalKey,
            cancellationToken);
        CatalogIngestionOutcomeStateContract outcomeState;
        if (draft is null)
        {
            draft = new CatalogIngestionDraftRow
            {
                ListingId = Guid.CreateVersion7(),
                ListingRevisionId = Guid.CreateVersion7(),
                IngestionBatchId = command.IngestionBatchId,
                IngestionItemKey = command.IngestionItemKey,
                SiteKey = command.SiteKey,
                CatalogKey = command.CatalogKey,
                CatalogConfigurationRevisionId = command.ExpectedCatalogConfigurationRevisionId,
                EntityKind = command.EntityKind,
                SubjectNaturalKey = command.SubjectNaturalKey,
                FieldsDocument = fieldsDocument,
                FieldsDigest = fieldsDigest,
                AggregateRevision = 1,
                CreatedAtUtc = command.RequestedAtUtc,
                LastChangedAtUtc = command.RequestedAtUtc,
            };
            dbContext.Drafts.Add(draft);
            outcomeState = CatalogIngestionOutcomeStateContract.DraftCreated;
        }
        else
        {
            if (draft.CatalogConfigurationRevisionId != command.ExpectedCatalogConfigurationRevisionId)
            {
                throw DraftFailure(
                    "CATALOG_INGESTION_DRAFT_CONFIGURATION_CONFLICT",
                    409,
                    "The existing draft belongs to a different Catalog configuration revision.",
                    "Review or migrate the existing draft before importing another revision.");
            }

            draft.ListingRevisionId = Guid.CreateVersion7();
            draft.IngestionBatchId = command.IngestionBatchId;
            draft.IngestionItemKey = command.IngestionItemKey;
            draft.FieldsDocument = fieldsDocument;
            draft.FieldsDigest = fieldsDigest;
            draft.AggregateRevision++;
            draft.LastChangedAtUtc = command.RequestedAtUtc;
            outcomeState = CatalogIngestionOutcomeStateContract.DraftUpdated;
        }

        var outcome = new CatalogIngestionCommandOutcome(
            command.CommandId,
            command.IngestionBatchId,
            command.IngestionItemKey,
            outcomeState,
            draft.ListingId,
            draft.ListingRevisionId,
            FailureCode: null,
            FailureDetail: null,
            command.RequestedAtUtc);
        var resultDocument = CatalogIngestionDocument.Serialize(outcome);
        dbContext.Commands.Add(new CatalogIngestionCommandRow
        {
            CommandId = command.CommandId,
            CommandDigest = command.CommandDigest,
            IngestionBatchId = command.IngestionBatchId,
            IngestionItemKey = command.IngestionItemKey,
            ResultDocument = resultDocument,
            ResultDigest = CatalogIngestionDocument.ComputeDigest(resultDocument),
            CallerIdentity = callerIdentity,
            CreatedAtUtc = command.RequestedAtUtc,
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return outcome;
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            replay = await ReadReplayAsync(command, cancellationToken);
            if (replay is not null)
            {
                return replay;
            }

            throw DraftFailure(
                "CATALOG_INGESTION_DRAFT_IDENTITY_CONFLICT",
                409,
                "The draft command conflicts with another Catalog-owned natural or command identity.",
                "Read the current draft and replay the exact original command.",
                exception);
        }
    }

    private async Task<CatalogIngestionCommandOutcome?> ReadReplayAsync(
        CatalogIngestionUpsertDraftCommand command,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.Commands
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.CommandId == command.CommandId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        if (!string.Equals(row.CommandDigest, command.CommandDigest, StringComparison.Ordinal) ||
            row.IngestionBatchId != command.IngestionBatchId ||
            !string.Equals(row.IngestionItemKey, command.IngestionItemKey, StringComparison.Ordinal))
        {
            throw DraftFailure(
                "CATALOG_INGESTION_COMMAND_ID_CONFLICT",
                409,
                "The command identity was already used for another Catalog draft request.",
                "Replay the exact original command or use a new command identity.");
        }

        var actualDigest = CatalogIngestionDocument.ComputeDigest(row.ResultDocument);
        if (!string.Equals(actualDigest, row.ResultDigest, StringComparison.Ordinal))
        {
            throw DraftFailure(
                "CATALOG_INGESTION_RESULT_DIGEST_MISMATCH",
                500,
                "A persisted Catalog ingestion result failed digest verification.",
                "Restore the result from a verified Catalog database backup.");
        }

        return CatalogIngestionDocument.Deserialize<CatalogIngestionCommandOutcome>(row.ResultDocument);
    }

    private static CatalogIngestionDraftException DraftFailure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        Exception? innerException = null) =>
        new(
            "Catalog.Ingestion",
            code,
            statusCode,
            detail,
            requiredAction,
            innerException: innerException);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        };
}

public static class CatalogIngestionInfrastructureExtensions
{
    public static IServiceCollection AddCatalogIngestionInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Catalog")
            ?? throw new InvalidOperationException("Connection string 'Catalog' is required.");
        services.AddDbContext<CatalogIngestionDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<ICatalogIngestionDraftStore, EfCatalogIngestionDraftStore>();
        services.AddScoped<ICatalogIngestionTargetProjectionWriter, EfCatalogIngestionTargetProjectionWriter>();
        services.AddScoped<ICatalogIngestionTargetProjectionWriter, EfCatalogIngestionTargetProjectionWriter>();
        services.AddScoped<ICatalogIngestionTargetProjectionWriter, EfCatalogIngestionTargetProjectionWriter>();
        return services;
    }
}

internal static class CatalogIngestionDocument
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> document)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(document, Options)
                ?? throw new CatalogIngestionDraftException(
                    "Catalog.Ingestion",
                    "CATALOG_INGESTION_DOCUMENT_NULL",
                    500,
                    "A persisted ingestion document deserialized to null.",
                    "Restore the document from a verified Catalog database backup.");
        }
        catch (JsonException exception)
        {
            throw new CatalogIngestionDraftException(
                "Catalog.Ingestion",
                "CATALOG_INGESTION_DOCUMENT_INVALID",
                500,
                "A persisted ingestion document is invalid for its owner contract.",
                "Restore the document from a verified Catalog database backup.",
                innerException: exception);
        }
    }

    public static string ComputeDigest(ReadOnlySpan<byte> document)
    {
        if (document.IsEmpty)
        {
            throw new CatalogIngestionDraftException(
                "Catalog.Ingestion",
                "CATALOG_INGESTION_DOCUMENT_EMPTY",
                500,
                "A persisted ingestion document cannot be empty.",
                "Restore the document from a verified Catalog database backup.");
        }

        return Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }
}

internal sealed class CatalogIngestionTargetRow
{
    public string SiteKey { get; set; } = string.Empty;
    public string CatalogKey { get; set; } = string.Empty;
    public Guid ActiveConfigurationRevisionId { get; set; }
    public long ProjectionRevision { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

internal sealed class CatalogIngestionDraftRow
{
    public Guid ListingId { get; set; }
    public Guid ListingRevisionId { get; set; }
    public Guid IngestionBatchId { get; set; }
    public string IngestionItemKey { get; set; } = string.Empty;
    public string SiteKey { get; set; } = string.Empty;
    public string CatalogKey { get; set; } = string.Empty;
    public Guid CatalogConfigurationRevisionId { get; set; }
    public string EntityKind { get; set; } = string.Empty;
    public string SubjectNaturalKey { get; set; } = string.Empty;
    public byte[] FieldsDocument { get; set; } = [];
    public string FieldsDigest { get; set; } = string.Empty;
    public long AggregateRevision { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastChangedAtUtc { get; set; }
}

internal sealed class CatalogIngestionCommandRow
{
    public Guid CommandId { get; set; }
    public string CommandDigest { get; set; } = string.Empty;
    public Guid IngestionBatchId { get; set; }
    public string IngestionItemKey { get; set; } = string.Empty;
    public byte[] ResultDocument { get; set; } = [];
    public string ResultDigest { get; set; } = string.Empty;
    public string CallerIdentity { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
