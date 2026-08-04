using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Aggregator.Promotion.Infrastructure;

public sealed class PromotionRuntimeDbContext(DbContextOptions<PromotionRuntimeDbContext> options)
    : DbContext(options)
{
    internal DbSet<PromotionCampaignRow> Campaigns => Set<PromotionCampaignRow>();

    internal DbSet<PromotionCommandRow> Commands => Set<PromotionCommandRow>();

    internal DbSet<PromotionEligibilityRow> Eligibility => Set<PromotionEligibilityRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ConfigureCampaign(modelBuilder);
        ConfigureCommand(modelBuilder);
        ConfigureEligibility(modelBuilder);
    }

    private static void ConfigureCampaign(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PromotionCampaignRow>();
        entity.ToTable("campaign", "promotion");
        entity.HasKey(row => row.Id);
        entity.Property(row => row.Id).HasColumnName("id");
        entity.Property(row => row.ProductRevisionId).HasColumnName("product_revision_id");
        entity.Property(row => row.EntitlementId).HasColumnName("entitlement_id");
        entity.Property(row => row.ListingId).HasColumnName("listing_id");
        entity.Property(row => row.CatalogKey).HasColumnName("catalog_key").HasMaxLength(96);
        entity.Property(row => row.PlacementKey).HasColumnName("placement_key").HasMaxLength(96);
        entity.Property(row => row.CapacityUnits).HasColumnName("capacity_units");
        entity.Property(row => row.StartsAtUtc)
            .HasColumnName("starts_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.Property(row => row.EndsAtUtc)
            .HasColumnName("ends_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.Property(row => row.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.Property(row => row.LastChangedAtUtc)
            .HasColumnName("last_changed_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.Property(row => row.State).HasColumnName("state");
        entity.Property(row => row.AggregateRevision)
            .HasColumnName("aggregate_revision")
            .IsConcurrencyToken();
        entity.Property(row => row.SuspensionReason)
            .HasColumnName("suspension_reason")
            .HasMaxLength(300);
        entity.HasIndex(row => new
        {
            row.CatalogKey,
            row.PlacementKey,
            row.State,
            row.StartsAtUtc,
            row.EndsAtUtc,
        });
        entity.HasIndex(row => new { row.ListingId, row.State });
        entity.ToTable(table =>
        {
            table.HasCheckConstraint("ck_promotion_campaign_state", "state BETWEEN 1 AND 5");
            table.HasCheckConstraint("ck_promotion_campaign_capacity", "capacity_units BETWEEN 1 AND 100");
            table.HasCheckConstraint("ck_promotion_campaign_window", "ends_at_utc > starts_at_utc");
            table.HasCheckConstraint(
                "ck_promotion_campaign_time_order",
                "last_changed_at_utc >= created_at_utc");
            table.HasCheckConstraint(
                "ck_promotion_campaign_revision",
                "aggregate_revision > 0");
            table.HasCheckConstraint(
                "ck_promotion_campaign_suspension_reason",
                "(state = 3 AND suspension_reason IS NOT NULL) OR (state <> 3 AND suspension_reason IS NULL)");
        });
    }

    private static void ConfigureCommand(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PromotionCommandRow>();
        entity.ToTable("command_result", "promotion_operations");
        entity.HasKey(row => new { row.Scope, row.Key });
        entity.Property(row => row.Scope).HasColumnName("scope").HasMaxLength(150);
        entity.Property(row => row.Key).HasColumnName("key").HasMaxLength(200);
        entity.Property(row => row.RequestDigest)
            .HasColumnName("request_digest")
            .HasMaxLength(64)
            .IsFixedLength();
        entity.Property(row => row.CampaignId).HasColumnName("campaign_id");
        entity.Property(row => row.ResultDocument)
            .HasColumnName("result_document")
            .HasColumnType("bytea");
        entity.Property(row => row.ResultDigest)
            .HasColumnName("result_digest")
            .HasMaxLength(64)
            .IsFixedLength();
        entity.Property(row => row.CallerIdentity)
            .HasColumnName("caller_identity")
            .HasMaxLength(200);
        entity.Property(row => row.CreatedAtUtc)
            .HasColumnName("created_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.HasOne<PromotionCampaignRow>()
            .WithMany()
            .HasForeignKey(row => row.CampaignId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_promotion_command_request_digest",
                "request_digest ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_promotion_command_result_digest",
                "result_digest ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "ck_promotion_command_result_document",
                "octet_length(result_document) > 0");
        });
    }

    private static void ConfigureEligibility(ModelBuilder modelBuilder)
    {
        var entity = modelBuilder.Entity<PromotionEligibilityRow>();
        entity.ToTable("eligibility", "promotion_projection");
        entity.HasKey(row => new
        {
            row.ProductRevisionId,
            row.EntitlementId,
            row.ListingId,
            row.CatalogKey,
            row.PlacementKey,
        });
        entity.Property(row => row.ProductRevisionId).HasColumnName("product_revision_id");
        entity.Property(row => row.ProductRevisionActive).HasColumnName("product_revision_active");
        entity.Property(row => row.EntitlementId).HasColumnName("entitlement_id");
        entity.Property(row => row.EntitlementActive).HasColumnName("entitlement_active");
        entity.Property(row => row.ListingId).HasColumnName("listing_id");
        entity.Property(row => row.ListingEligible).HasColumnName("listing_eligible");
        entity.Property(row => row.CatalogKey).HasColumnName("catalog_key").HasMaxLength(96);
        entity.Property(row => row.PlacementKey).HasColumnName("placement_key").HasMaxLength(96);
        entity.Property(row => row.PlacementCapacityLimit).HasColumnName("placement_capacity_limit");
        entity.Property(row => row.ProjectionRevision)
            .HasColumnName("projection_revision")
            .IsConcurrencyToken();
        entity.Property(row => row.UpdatedAtUtc)
            .HasColumnName("updated_at_utc")
            .HasColumnType("timestamp with time zone");
        entity.HasIndex(row => new { row.CatalogKey, row.PlacementKey });
        entity.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_promotion_eligibility_capacity",
                "placement_capacity_limit BETWEEN 1 AND 10000");
            table.HasCheckConstraint(
                "ck_promotion_eligibility_revision",
                "projection_revision > 0");
        });
    }
}

public sealed class EfPromotionCampaignStore(PromotionRuntimeDbContext dbContext)
    : IPromotionCampaignStore,
      IPromotionCommandResultReader
{
    public async Task<PromotionCampaignCommandResult> CreateAsync(
        PromotionCampaign campaign,
        int placementCapacityLimit,
        PromotionCommandIdentity commandIdentity,
        string callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ValidateCommand(commandIdentity, callerIdentity);
        if (placementCapacityLimit is < 1 or > 10_000)
        {
            throw PersistenceFailure(
                "PROMOTION_CAPACITY_LIMIT_INVALID",
                500,
                "The projected placement capacity limit is invalid.",
                "Repair the Promotion eligibility projection.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var replay = await ReadCommandResultCoreAsync(commandIdentity, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PromotionCampaignCommandResult(replay, Replayed: true);
        }

        var projection = await dbContext.Eligibility
            .AsNoTracking()
            .SingleOrDefaultAsync(row =>
                row.ProductRevisionId == campaign.ProductRevisionId &&
                row.EntitlementId == campaign.EntitlementId &&
                row.ListingId == campaign.ListingId &&
                row.CatalogKey == campaign.CatalogKey &&
                row.PlacementKey == campaign.PlacementKey,
                cancellationToken)
            ?? throw PersistenceFailure(
                "PROMOTION_ELIGIBILITY_PROJECTION_MISSING",
                409,
                "The exact Promotion eligibility projection is missing during capacity reservation.",
                "Wait for the producer events and retry the exact campaign command.");
        if (projection.PlacementCapacityLimit != placementCapacityLimit)
        {
            throw PersistenceFailure(
                "PROMOTION_CAPACITY_PROJECTION_CHANGED",
                409,
                "Placement capacity changed before the campaign reservation was persisted.",
                "Reload the current eligibility projection and retry.");
        }

        var reservedUnits = await dbContext.Campaigns
            .AsNoTracking()
            .Where(row =>
                row.CatalogKey == campaign.CatalogKey &&
                row.PlacementKey == campaign.PlacementKey &&
                row.State <= (int)PromotionCampaignState.Suspended &&
                row.StartsAtUtc < campaign.EndsAtUtc &&
                row.EndsAtUtc > campaign.StartsAtUtc)
            .SumAsync(row => (int?)row.CapacityUnits, cancellationToken)
            ?? 0;
        if (reservedUnits + campaign.CapacityUnits > placementCapacityLimit)
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Capacity",
                "PROMOTION_PLACEMENT_CAPACITY_EXCEEDED",
                409,
                "The requested campaign window exceeds the exact sponsored placement capacity.",
                "Reduce capacity units, choose another window, or wait for a reservation to complete.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = campaign.CatalogKey,
                    ["placementKey"] = campaign.PlacementKey,
                    ["capacityLimit"] = placementCapacityLimit,
                    ["reservedUnits"] = reservedUnits,
                    ["requestedUnits"] = campaign.CapacityUnits,
                });
        }

        var snapshot = PromotionCampaignSnapshot.From(campaign);
        dbContext.Campaigns.Add(ToRow(campaign));
        dbContext.Commands.Add(ToCommandRow(
            commandIdentity,
            snapshot,
            callerIdentity,
            campaign.CreatedAtUtc));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PromotionCampaignCommandResult(snapshot, Replayed: false);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            replay = await ReadCommandResultCoreAsync(commandIdentity, cancellationToken);
            if (replay is not null)
            {
                return new PromotionCampaignCommandResult(replay, Replayed: true);
            }

            throw PersistenceFailure(
                "PROMOTION_CAMPAIGN_IDENTITY_CONFLICT",
                409,
                "The campaign command conflicts with an existing Promotion-owned identity.",
                "Read the existing campaign or retry with a new command identity.",
                exception);
        }
    }

    public async Task<PromotionCampaignSnapshot?> ReadAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {
        if (campaignId == Guid.Empty)
        {
            throw PersistenceFailure(
                "PROMOTION_CAMPAIGN_ID_REQUIRED",
                400,
                "A campaign ID is required.",
                "Provide the exact campaign identity returned by creation.");
        }

        var row = await dbContext.Campaigns
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == campaignId, cancellationToken);
        return row is null ? null : ToSnapshot(row);
    }

    public async Task<PromotionCampaignCommandResult> SaveAsync(
        PromotionCampaign campaign,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        string callerIdentity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(campaign);
        ValidateCommand(commandIdentity, callerIdentity);
        if (expectedStoredAggregateRevision <= 0 ||
            campaign.AggregateRevision != expectedStoredAggregateRevision + 1)
        {
            throw PersistenceFailure(
                "PROMOTION_TRANSITION_REVISION_INVALID",
                500,
                "One Promotion lifecycle command must advance the aggregate revision exactly once.",
                "Execute one domain transition per persisted command.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        var replay = await ReadCommandResultCoreAsync(commandIdentity, cancellationToken);
        if (replay is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PromotionCampaignCommandResult(replay, Replayed: true);
        }

        var row = await dbContext.Campaigns
            .SingleOrDefaultAsync(candidate => candidate.Id == campaign.Id, cancellationToken)
            ?? throw PersistenceFailure(
                "PROMOTION_CAMPAIGN_NOT_FOUND",
                404,
                $"Promotion campaign '{campaign.Id:D}' was not found.",
                "Use the exact campaign identity returned by creation.");
        if (row.AggregateRevision != expectedStoredAggregateRevision)
        {
            throw RevisionConflict(
                campaign.Id,
                expectedStoredAggregateRevision,
                row.AggregateRevision);
        }

        ValidateImmutableIdentity(row, campaign);
        row.LastChangedAtUtc = campaign.LastChangedAtUtc;
        row.State = (int)campaign.State;
        row.AggregateRevision = campaign.AggregateRevision;
        row.SuspensionReason = campaign.SuspensionReason;
        var snapshot = PromotionCampaignSnapshot.From(campaign);
        dbContext.Commands.Add(ToCommandRow(
            commandIdentity,
            snapshot,
            callerIdentity,
            campaign.LastChangedAtUtc));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PromotionCampaignCommandResult(snapshot, Replayed: false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var actualRevision = await dbContext.Campaigns
                .AsNoTracking()
                .Where(candidate => candidate.Id == campaign.Id)
                .Select(candidate => (long?)candidate.AggregateRevision)
                .SingleOrDefaultAsync(cancellationToken);
            throw RevisionConflict(
                campaign.Id,
                expectedStoredAggregateRevision,
                actualRevision,
                exception);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            replay = await ReadCommandResultCoreAsync(commandIdentity, cancellationToken);
            if (replay is not null)
            {
                return new PromotionCampaignCommandResult(replay, Replayed: true);
            }

            throw PersistenceFailure(
                "PROMOTION_COMMAND_IDENTITY_CONFLICT",
                409,
                "The lifecycle command conflicts with an existing Promotion command identity.",
                "Read the existing result or retry with a new exact Idempotency-Key.",
                exception);
        }
    }

    public async Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadActiveAsync(
        string catalogKey,
        string placementKey,
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Campaigns
            .AsNoTracking()
            .Where(row =>
                row.CatalogKey == catalogKey &&
                row.PlacementKey == placementKey &&
                row.State == (int)PromotionCampaignState.Active &&
                row.StartsAtUtc <= effectiveAtUtc &&
                row.EndsAtUtc > effectiveAtUtc)
            .OrderBy(row => row.StartsAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToSnapshot).ToArray();
    }

    public async Task<IReadOnlyList<PromotionCampaignSnapshot>> ReadExpiredAsync(
        DateTimeOffset effectiveAtUtc,
        int limit,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.Campaigns
            .AsNoTracking()
            .Where(row =>
                (row.State == (int)PromotionCampaignState.Active ||
                 row.State == (int)PromotionCampaignState.Suspended) &&
                row.EndsAtUtc <= effectiveAtUtc)
            .OrderBy(row => row.EndsAtUtc)
            .ThenBy(row => row.Id)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return rows.Select(ToSnapshot).ToArray();
    }

    public Task<PromotionCampaignSnapshot?> ReadCommandResultAsync(
        PromotionCommandIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return ReadCommandResultCoreAsync(identity, cancellationToken);
    }

    private async Task<PromotionCampaignSnapshot?> ReadCommandResultCoreAsync(
        PromotionCommandIdentity identity,
        CancellationToken cancellationToken)
    {
        var command = await dbContext.Commands
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.Scope == identity.Scope && row.Key == identity.Key,
                cancellationToken);
        if (command is null)
        {
            return null;
        }

        if (!string.Equals(command.RequestDigest, identity.RequestDigest, StringComparison.Ordinal))
        {
            throw new PromotionCampaignApplicationException(
                "Promotion.Commands",
                "PROMOTION_IDEMPOTENCY_DIGEST_CONFLICT",
                409,
                "The Idempotency-Key was already used for a different Promotion request.",
                "Replay the exact original request or use a new stable key.");
        }

        var actualDigest = PromotionDocument.ComputeDigest(command.ResultDocument);
        if (!string.Equals(actualDigest, command.ResultDigest, StringComparison.Ordinal))
        {
            throw PersistenceFailure(
                "PROMOTION_COMMAND_RESULT_DIGEST_MISMATCH",
                500,
                "A persisted Promotion command result failed digest verification.",
                "Restore the command result from a verified Promotion database backup.");
        }

        var snapshot = PromotionDocument.Deserialize(command.ResultDocument);
        if (snapshot.Id != command.CampaignId)
        {
            throw PersistenceFailure(
                "PROMOTION_COMMAND_RESULT_IDENTITY_MISMATCH",
                500,
                "A persisted Promotion command result identifies a different campaign.",
                "Restore the command result from a verified Promotion database backup.");
        }

        return snapshot;
    }

    private static PromotionCampaignRow ToRow(PromotionCampaign campaign) =>
        new()
        {
            Id = campaign.Id,
            ProductRevisionId = campaign.ProductRevisionId,
            EntitlementId = campaign.EntitlementId,
            ListingId = campaign.ListingId,
            CatalogKey = campaign.CatalogKey,
            PlacementKey = campaign.PlacementKey,
            CapacityUnits = campaign.CapacityUnits,
            StartsAtUtc = campaign.StartsAtUtc,
            EndsAtUtc = campaign.EndsAtUtc,
            CreatedAtUtc = campaign.CreatedAtUtc,
            LastChangedAtUtc = campaign.LastChangedAtUtc,
            State = (int)campaign.State,
            AggregateRevision = campaign.AggregateRevision,
            SuspensionReason = campaign.SuspensionReason,
        };

    private static PromotionCampaignSnapshot ToSnapshot(PromotionCampaignRow row)
    {
        if (!Enum.IsDefined(typeof(PromotionCampaignState), row.State))
        {
            throw PersistenceFailure(
                "PROMOTION_CAMPAIGN_STATE_CORRUPT",
                500,
                $"Campaign '{row.Id:D}' contains unsupported state value '{row.State}'.",
                "Repair the campaign through an owner migration or restore operation.");
        }

        return new PromotionCampaignSnapshot(
            row.Id,
            row.ProductRevisionId,
            row.EntitlementId,
            row.ListingId,
            row.CatalogKey,
            row.PlacementKey,
            row.CapacityUnits,
            row.StartsAtUtc,
            row.EndsAtUtc,
            row.CreatedAtUtc,
            row.LastChangedAtUtc,
            (PromotionCampaignState)row.State,
            row.AggregateRevision,
            row.SuspensionReason);
    }

    private static PromotionCommandRow ToCommandRow(
        PromotionCommandIdentity identity,
        PromotionCampaignSnapshot snapshot,
        string callerIdentity,
        DateTimeOffset createdAtUtc)
    {
        var document = PromotionDocument.Serialize(snapshot);
        return new PromotionCommandRow
        {
            Scope = identity.Scope,
            Key = identity.Key,
            RequestDigest = identity.RequestDigest,
            CampaignId = snapshot.Id,
            ResultDocument = document,
            ResultDigest = PromotionDocument.ComputeDigest(document),
            CallerIdentity = callerIdentity,
            CreatedAtUtc = createdAtUtc,
        };
    }

    private static void ValidateCommand(
        PromotionCommandIdentity identity,
        string callerIdentity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(callerIdentity) || callerIdentity.Length > 200)
        {
            throw PersistenceFailure(
                "PROMOTION_CALLER_IDENTITY_INVALID",
                500,
                "The command caller identity is invalid.",
                "Correct the authenticated Promotion workload mapping.");
        }
    }

    private static void ValidateImmutableIdentity(
        PromotionCampaignRow row,
        PromotionCampaign campaign)
    {
        var consistent = row.ProductRevisionId == campaign.ProductRevisionId &&
            row.EntitlementId == campaign.EntitlementId &&
            row.ListingId == campaign.ListingId &&
            row.CatalogKey == campaign.CatalogKey &&
            row.PlacementKey == campaign.PlacementKey &&
            row.CapacityUnits == campaign.CapacityUnits &&
            row.StartsAtUtc == campaign.StartsAtUtc &&
            row.EndsAtUtc == campaign.EndsAtUtc &&
            row.CreatedAtUtc == campaign.CreatedAtUtc;
        if (!consistent)
        {
            throw PersistenceFailure(
                "PROMOTION_CAMPAIGN_IDENTITY_MISMATCH",
                500,
                "The lifecycle aggregate does not match the immutable persisted campaign identity.",
                "Reload the exact campaign before applying a transition.");
        }
    }

    private static PromotionCampaignApplicationException RevisionConflict(
        Guid campaignId,
        long expectedRevision,
        long? actualRevision,
        Exception? innerException = null) =>
        new(
            "Promotion.Campaigns",
            "PROMOTION_REVISION_CONFLICT",
            409,
            "The campaign changed before the command was persisted.",
            "Reload the current campaign and retry with its exact aggregate revision.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["campaignId"] = campaignId,
                ["expectedRevision"] = expectedRevision,
                ["actualRevision"] = actualRevision,
            },
            innerException);

    private static PromotionCampaignApplicationException PersistenceFailure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        Exception? innerException = null) =>
        new(
            "Promotion.Persistence",
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

public sealed class EfPromotionEligibilityReader(PromotionRuntimeDbContext dbContext)
    : IPromotionEligibilityReader
{
    public async Task<PromotionEligibilitySnapshot?> ReadAsync(
        Guid productRevisionId,
        Guid entitlementId,
        Guid listingId,
        string catalogKey,
        string placementKey,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.Eligibility
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate =>
                candidate.ProductRevisionId == productRevisionId &&
                candidate.EntitlementId == entitlementId &&
                candidate.ListingId == listingId &&
                candidate.CatalogKey == catalogKey &&
                candidate.PlacementKey == placementKey,
                cancellationToken);
        return row is null
            ? null
            : new PromotionEligibilitySnapshot(
                row.ProductRevisionId,
                row.ProductRevisionActive,
                row.EntitlementId,
                row.EntitlementActive,
                row.ListingId,
                row.ListingEligible,
                row.CatalogKey,
                row.PlacementKey,
                row.PlacementCapacityLimit,
                row.ProjectionRevision);
    }
}

public static class PromotionRuntimeInfrastructureExtensions
{
    public static IServiceCollection AddPromotionRuntimeInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var connectionString = configuration.GetConnectionString("Promotion")
            ?? throw new InvalidOperationException("Connection string 'Promotion' is required.");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'Promotion' cannot be empty.");
        }

        services.AddDbContext<PromotionRuntimeDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddScoped<EfPromotionCampaignStore>();
        services.AddScoped<IPromotionCampaignStore>(provider =>
            provider.GetRequiredService<EfPromotionCampaignStore>());
        services.AddScoped<IPromotionCommandResultReader>(provider =>
            provider.GetRequiredService<EfPromotionCampaignStore>());
        services.AddScoped<IPromotionEligibilityReader, EfPromotionEligibilityReader>();
        services.AddScoped<PromotionRuntimeReadinessProbe>();
        return services;
    }
}

public sealed class PromotionRuntimeReadinessProbe(PromotionRuntimeDbContext dbContext)
{
    public Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
        dbContext.Database.CanConnectAsync(cancellationToken);
}

internal static class PromotionDocument
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize(PromotionCampaignSnapshot snapshot) =>
        JsonSerializer.SerializeToUtf8Bytes(snapshot, Options);

    public static PromotionCampaignSnapshot Deserialize(ReadOnlySpan<byte> document)
    {
        try
        {
            return JsonSerializer.Deserialize<PromotionCampaignSnapshot>(document, Options)
                ?? throw PersistenceFailure(
                    "PROMOTION_COMMAND_RESULT_NULL",
                    "A persisted command result deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw PersistenceFailure(
                "PROMOTION_COMMAND_RESULT_INVALID",
                "A persisted command result is invalid for its owner contract.",
                exception);
        }
    }

    public static string ComputeDigest(ReadOnlySpan<byte> document)
    {
        if (document.IsEmpty)
        {
            throw PersistenceFailure(
                "PROMOTION_COMMAND_RESULT_EMPTY",
                "A persisted command result cannot be empty.");
        }

        return Convert.ToHexString(SHA256.HashData(document)).ToLowerInvariant();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private static PromotionCampaignApplicationException PersistenceFailure(
        string code,
        string detail,
        Exception? innerException = null) =>
        new(
            "Promotion.Persistence",
            code,
            500,
            detail,
            "Restore the exact command result from a verified Promotion database backup.",
            innerException: innerException);
}

internal sealed class PromotionCampaignRow
{
    public Guid Id { get; set; }

    public Guid ProductRevisionId { get; set; }

    public Guid EntitlementId { get; set; }

    public Guid ListingId { get; set; }

    public string CatalogKey { get; set; } = string.Empty;

    public string PlacementKey { get; set; } = string.Empty;

    public int CapacityUnits { get; set; }

    public DateTimeOffset StartsAtUtc { get; set; }

    public DateTimeOffset EndsAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastChangedAtUtc { get; set; }

    public int State { get; set; }

    public long AggregateRevision { get; set; }

    public string? SuspensionReason { get; set; }
}

internal sealed class PromotionCommandRow
{
    public string Scope { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    public string RequestDigest { get; set; } = string.Empty;

    public Guid CampaignId { get; set; }

    public byte[] ResultDocument { get; set; } = [];

    public string ResultDigest { get; set; } = string.Empty;

    public string CallerIdentity { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }
}

internal sealed class PromotionEligibilityRow
{
    public Guid ProductRevisionId { get; set; }

    public bool ProductRevisionActive { get; set; }

    public Guid EntitlementId { get; set; }

    public bool EntitlementActive { get; set; }

    public Guid ListingId { get; set; }

    public bool ListingEligible { get; set; }

    public string CatalogKey { get; set; } = string.Empty;

    public string PlacementKey { get; set; } = string.Empty;

    public int PlacementCapacityLimit { get; set; }

    public long ProjectionRevision { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
