using System.Data;
using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Overlay.Application;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Promotion.Overlay.Infrastructure;

public static class PromotionOverlayInfrastructureExtensions
{
    public static IServiceCollection AddPromotionOverlayInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<IPromotionOverlayStore, PostgresPromotionOverlayStore>();
        services.AddSingleton<IPromotionOverlayIdSource, UuidV7PromotionOverlayIdSource>();
        services.AddScoped<IPromotionOverlayOutboxStore, PostgresPromotionOverlayOutboxStore>();
        return services;
    }
}

public sealed class PostgresPromotionOverlayStore : IPromotionOverlayStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPromotionOverlayStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<long> GetNextActivationRevisionAsync(
        string catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO promotion.overlay_activation_sequence (catalog_key, next_revision)
            VALUES (@catalog_key, 2)
            ON CONFLICT (catalog_key)
            DO UPDATE SET next_revision = promotion.overlay_activation_sequence.next_revision + 1
            RETURNING next_revision - 1;
            """);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "Promotion overlay activation revision allocator returned no value.");
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<PromotionOverlayCommitResult> CommitAsync(
        PromotionOverlayPublication publication,
        Guid? expectedCurrentOverlayId,
        string commandDigest,
        PromotionOverlayOutboxMessage outboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandDigest);
        ArgumentNullException.ThrowIfNull(outboxMessage);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var replay = await ReadCommandReplayAsync(
            connection,
            transaction,
            publication.CommandId,
            cancellationToken);
        if (replay is not null)
        {
            if (!string.Equals(replay.Value.CommandDigest, commandDigest, StringComparison.Ordinal))
            {
                throw Failure(
                    "PROMOTION_COMMAND_ID_REUSED",
                    409,
                    $"Promotion command '{publication.CommandId}' was already committed with different content.",
                    "Generate a new command ID for changed content; replay only the exact original command.");
            }

            var existing = await ReadPublicationAsync(
                connection,
                transaction,
                replay.Value.OverlayId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PromotionOverlayCommitResult(existing, Replayed: true);
        }

        var currentOverlayId = await ReadCurrentOverlayIdAsync(
            connection,
            transaction,
            publication.CatalogKey,
            cancellationToken);
        if (currentOverlayId != expectedCurrentOverlayId)
        {
            throw Failure(
                "PROMOTION_POINTER_CONFLICT",
                409,
                $"Catalog '{publication.CatalogKey}' expected current overlay '{Render(expectedCurrentOverlayId)}' but is at '{Render(currentOverlayId)}'.",
                "Reload the current Promotion pointer and publish against its exact identity.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = publication.CatalogKey,
                    ["expectedCurrentOverlayId"] = expectedCurrentOverlayId,
                    ["actualCurrentOverlayId"] = currentOverlayId,
                });
        }

        await InsertPublicationAsync(connection, transaction, publication, cancellationToken);
        await UpsertCurrentPointerAsync(connection, transaction, publication, cancellationToken);
        await InsertCommandAsync(
            connection,
            transaction,
            publication.CommandId,
            commandDigest,
            publication.OverlayId,
            publication.CreatedAtUtc,
            cancellationToken);
        await InsertOutboxAsync(connection, transaction, outboxMessage, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PromotionOverlayCommitResult(publication, Replayed: false);
    }

    private static async Task<(string CommandDigest, Guid OverlayId)?> ReadCommandReplayAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT command_digest, overlay_id
            FROM promotion.overlay_command
            WHERE command_id = @command_id
            FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, commandId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetGuid(1));
    }

    private static async Task<Guid?> ReadCurrentOverlayIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT overlay_id
            FROM promotion.current_overlay
            WHERE catalog_key = @catalog_key
            FOR UPDATE;
            """, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (Guid)value;
    }

    private static async Task InsertPublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionOverlayPublication publication,
        CancellationToken cancellationToken)
    {
        await using (var command = new NpgsqlCommand("""
            INSERT INTO promotion.overlay_publication
            (
                overlay_id,
                command_id,
                catalog_key,
                source_public_read_revision_id,
                activation_revision,
                content_digest,
                created_at_utc
            )
            VALUES
            (
                @overlay_id,
                @command_id,
                @catalog_key,
                @source_public_read_revision_id,
                @activation_revision,
                @content_digest,
                @created_at_utc
            );
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, publication.OverlayId);
            command.Parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, publication.CommandId);
            command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, publication.CatalogKey);
            command.Parameters.AddWithValue(
                "source_public_read_revision_id",
                NpgsqlDbType.Uuid,
                publication.SourcePublicReadRevisionId);
            command.Parameters.AddWithValue(
                "activation_revision",
                NpgsqlDbType.Bigint,
                publication.ActivationRevision);
            command.Parameters.AddWithValue("content_digest", NpgsqlDbType.Char, publication.ContentDigest);
            command.Parameters.AddWithValue(
                "created_at_utc",
                NpgsqlDbType.TimestampTz,
                publication.CreatedAtUtc);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var item in publication.Items)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO promotion.overlay_item
                (
                    overlay_id,
                    listing_id,
                    campaign_id,
                    position,
                    locale,
                    title,
                    route_path,
                    disclosure_label
                )
                VALUES
                (
                    @overlay_id,
                    @listing_id,
                    @campaign_id,
                    @position,
                    @locale,
                    @title,
                    @route_path,
                    @disclosure_label
                );
                """, connection, transaction);
            command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, publication.OverlayId);
            command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, item.ListingId);
            command.Parameters.AddWithValue("campaign_id", NpgsqlDbType.Uuid, item.CampaignId);
            command.Parameters.AddWithValue("position", NpgsqlDbType.Integer, item.Position);
            command.Parameters.AddWithValue("locale", NpgsqlDbType.Varchar, item.Locale);
            command.Parameters.AddWithValue("title", NpgsqlDbType.Varchar, item.Title);
            command.Parameters.AddWithValue("route_path", NpgsqlDbType.Varchar, item.RoutePath);
            command.Parameters.AddWithValue(
                "disclosure_label",
                NpgsqlDbType.Varchar,
                item.DisclosureLabel);
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpsertCurrentPointerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionOverlayPublication publication,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO promotion.current_overlay
            (
                catalog_key,
                overlay_id,
                source_public_read_revision_id,
                activation_revision,
                activated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @overlay_id,
                @source_public_read_revision_id,
                @activation_revision,
                @activated_at_utc
            )
            ON CONFLICT (catalog_key)
            DO UPDATE SET
                overlay_id = EXCLUDED.overlay_id,
                source_public_read_revision_id = EXCLUDED.source_public_read_revision_id,
                activation_revision = EXCLUDED.activation_revision,
                activated_at_utc = EXCLUDED.activated_at_utc;
            """, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, publication.CatalogKey);
        command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, publication.OverlayId);
        command.Parameters.AddWithValue(
            "source_public_read_revision_id",
            NpgsqlDbType.Uuid,
            publication.SourcePublicReadRevisionId);
        command.Parameters.AddWithValue(
            "activation_revision",
            NpgsqlDbType.Bigint,
            publication.ActivationRevision);
        command.Parameters.AddWithValue(
            "activated_at_utc",
            NpgsqlDbType.TimestampTz,
            publication.CreatedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertCommandAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid commandId,
        string commandDigest,
        Guid overlayId,
        DateTimeOffset committedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO promotion.overlay_command
            (command_id, command_digest, overlay_id, committed_at_utc)
            VALUES
            (@command_id, @command_digest, @overlay_id, @committed_at_utc);
            """, connection, transaction);
        command.Parameters.AddWithValue("command_id", NpgsqlDbType.Uuid, commandId);
        command.Parameters.AddWithValue("command_digest", NpgsqlDbType.Char, commandDigest);
        command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, overlayId);
        command.Parameters.AddWithValue(
            "committed_at_utc",
            NpgsqlDbType.TimestampTz,
            committedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PromotionOverlayOutboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO promotion.overlay_outbox
            (
                event_id,
                routing_key,
                contract_identity,
                payload_json,
                payload_digest,
                occurred_at_utc,
                correlation_id,
                causation_id,
                delivery_attempts
            )
            VALUES
            (
                @event_id,
                @routing_key,
                @contract_identity,
                CAST(@payload_json AS jsonb),
                @payload_digest,
                @occurred_at_utc,
                @correlation_id,
                @causation_id,
                0
            );
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, message.EventId);
        command.Parameters.AddWithValue("routing_key", NpgsqlDbType.Varchar, message.RoutingKey);
        command.Parameters.AddWithValue(
            "contract_identity",
            NpgsqlDbType.Varchar,
            message.ContractIdentity);
        command.Parameters.AddWithValue("payload_json", NpgsqlDbType.Text, message.PayloadJson);
        command.Parameters.AddWithValue("payload_digest", NpgsqlDbType.Char, message.PayloadDigest);
        command.Parameters.AddWithValue(
            "occurred_at_utc",
            NpgsqlDbType.TimestampTz,
            message.OccurredAtUtc);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, message.CorrelationId);
        command.Parameters.AddWithValue(
            "causation_id",
            NpgsqlDbType.Uuid,
            message.CausationId is { } causationId ? causationId : DBNull.Value);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<PromotionOverlayPublication> ReadPublicationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid overlayId,
        CancellationToken cancellationToken)
    {
        Guid commandId;
        string catalogKey;
        Guid sourceRevisionId;
        long activationRevision;
        string contentDigest;
        DateTimeOffset createdAtUtc;
        await using (var command = new NpgsqlCommand("""
            SELECT command_id,
                   catalog_key,
                   source_public_read_revision_id,
                   activation_revision,
                   content_digest,
                   created_at_utc
            FROM promotion.overlay_publication
            WHERE overlay_id = @overlay_id;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, overlayId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Promotion overlay '{overlayId}' was referenced by a command but not found.");
            }

            commandId = reader.GetGuid(0);
            catalogKey = reader.GetString(1);
            sourceRevisionId = reader.GetGuid(2);
            activationRevision = reader.GetInt64(3);
            contentDigest = reader.GetString(4);
            createdAtUtc = reader.GetFieldValue<DateTimeOffset>(5);
        }

        var items = new List<PromotionOverlayItemContract>();
        await using (var command = new NpgsqlCommand("""
            SELECT listing_id,
                   campaign_id,
                   position,
                   locale,
                   title,
                   route_path,
                   disclosure_label
            FROM promotion.overlay_item
            WHERE overlay_id = @overlay_id
            ORDER BY position;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("overlay_id", NpgsqlDbType.Uuid, overlayId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new PromotionOverlayItemContract(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }
        }

        return new PromotionOverlayPublication(
            commandId,
            overlayId,
            catalogKey,
            sourceRevisionId,
            activationRevision,
            contentDigest,
            items.AsReadOnly(),
            createdAtUtc);
    }

    private static string Render(Guid? value) => value?.ToString("D") ?? "absent";

    private static PromotionOverlayException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        IReadOnlyDictionary<string, object?>? context = null) =>
        new(code, statusCode, message, requiredAction, context);
}

public sealed record PromotionOverlayOutboxLease(
    Guid EventId,
    Guid LeaseToken,
    string RoutingKey,
    string ContractIdentity,
    string PayloadJson,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId,
    int DeliveryAttempts);

public interface IPromotionOverlayOutboxStore
{
    public Task<PromotionOverlayOutboxLease?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken);

    public Task MarkDispatchedAsync(
        Guid eventId,
        Guid leaseToken,
        DateTimeOffset dispatchedAtUtc,
        CancellationToken cancellationToken);

    public Task MarkFailedAsync(
        Guid eventId,
        Guid leaseToken,
        string failureMessage,
        DateTimeOffset failedAtUtc,
        int maximumAttempts,
        CancellationToken cancellationToken);
}

public sealed class PostgresPromotionOverlayOutboxStore : IPromotionOverlayOutboxStore
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresPromotionOverlayOutboxStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<PromotionOverlayOutboxLease?> LeaseNextAsync(
        string workerId,
        TimeSpan leaseDuration,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        if (leaseDuration < TimeSpan.FromSeconds(5) ||
            leaseDuration > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (maximumAttempts is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        var leaseToken = Guid.CreateVersion7();
        await using var command = _dataSource.CreateCommand("""
            WITH candidate AS
            (
                SELECT event_id
                FROM promotion.overlay_outbox
                WHERE dispatched_at_utc IS NULL
                  AND dead_lettered_at_utc IS NULL
                  AND delivery_attempts < @maximum_attempts
                  AND (lease_expires_at_utc IS NULL OR lease_expires_at_utc <= now())
                ORDER BY occurred_at_utc, event_id
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            )
            UPDATE promotion.overlay_outbox AS target
            SET lease_token = @lease_token,
                leased_by = @worker_id,
                lease_expires_at_utc = now() + @lease_duration,
                delivery_attempts = target.delivery_attempts + 1
            FROM candidate
            WHERE target.event_id = candidate.event_id
            RETURNING target.event_id,
                      target.routing_key,
                      target.contract_identity,
                      target.payload_json::text,
                      target.payload_digest,
                      target.occurred_at_utc,
                      target.correlation_id,
                      target.causation_id,
                      target.delivery_attempts;
            """);
        command.Parameters.AddWithValue("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts);
        command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, leaseToken);
        command.Parameters.AddWithValue("worker_id", NpgsqlDbType.Varchar, workerId);
        command.Parameters.AddWithValue("lease_duration", NpgsqlDbType.Interval, leaseDuration);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PromotionOverlayOutboxLease(
            reader.GetGuid(0),
            leaseToken,
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.GetInt32(8));
    }

    public async Task MarkDispatchedAsync(
        Guid eventId,
        Guid leaseToken,
        DateTimeOffset dispatchedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE promotion.overlay_outbox
            SET dispatched_at_utc = @dispatched_at_utc,
                lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL,
                last_error = NULL
            WHERE event_id = @event_id
              AND lease_token = @lease_token
              AND dispatched_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL;
            """);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, leaseToken);
        command.Parameters.AddWithValue(
            "dispatched_at_utc",
            NpgsqlDbType.TimestampTz,
            dispatchedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Promotion outbox event '{eventId}' was not owned by lease '{leaseToken}'.");
        }
    }

    public async Task MarkFailedAsync(
        Guid eventId,
        Guid leaseToken,
        string failureMessage,
        DateTimeOffset failedAtUtc,
        int maximumAttempts,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureMessage);
        await using var command = _dataSource.CreateCommand("""
            UPDATE promotion.overlay_outbox
            SET last_error = left(@last_error, 2000),
                dead_lettered_at_utc = CASE
                    WHEN delivery_attempts >= @maximum_attempts THEN @failed_at_utc
                    ELSE NULL
                END,
                dead_letter_reason = CASE
                    WHEN delivery_attempts >= @maximum_attempts THEN left(@last_error, 2000)
                    ELSE NULL
                END,
                lease_token = NULL,
                leased_by = NULL,
                lease_expires_at_utc = NULL
            WHERE event_id = @event_id
              AND lease_token = @lease_token
              AND dispatched_at_utc IS NULL
              AND dead_lettered_at_utc IS NULL;
            """);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        command.Parameters.AddWithValue("lease_token", NpgsqlDbType.Uuid, leaseToken);
        command.Parameters.AddWithValue(
            "last_error",
            NpgsqlDbType.Varchar,
            failureMessage);
        command.Parameters.AddWithValue("maximum_attempts", NpgsqlDbType.Integer, maximumAttempts);
        command.Parameters.AddWithValue("failed_at_utc", NpgsqlDbType.TimestampTz, failedAtUtc);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException(
                $"Promotion outbox event '{eventId}' failure was not owned by lease '{leaseToken}'.");
        }
    }
}
