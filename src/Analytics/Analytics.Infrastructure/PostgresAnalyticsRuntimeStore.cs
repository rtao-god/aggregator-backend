using System.Data;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Analytics.Infrastructure;

public static class AnalyticsRuntimeInfrastructureExtensions
{
    public static IServiceCollection AddAnalyticsRuntimeInfrastructure(
        this IServiceCollection services,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddScoped<IAnalyticsRuntimeStore, PostgresAnalyticsRuntimeStore>();
        return services;
    }
}

public sealed class PostgresAnalyticsRuntimeStore(NpgsqlDataSource dataSource) : IAnalyticsRuntimeStore
{
    public async Task<AnalyticsInteractionRegistration> RegisterAsync(
        AnalyticsInteractionRecord interaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var insertedAt = await TryInsertAsync(
            connection,
            transaction,
            interaction,
            cancellationToken);
        if (insertedAt is null)
        {
            var existing = await ReadExistingAsync(
                connection,
                transaction,
                interaction.EventId,
                cancellationToken);
            if (!string.Equals(existing.RequestDigest, interaction.RequestDigest, StringComparison.Ordinal))
            {
                throw new AnalyticsRuntimeException(
                    "ANALYTICS_EVENT_ID_REUSED",
                    409,
                    $"Interaction event '{interaction.EventId}' was already recorded with different content.",
                    "Generate a new event ID for changed content; replay only the exact original request.",
                    new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["eventId"] = interaction.EventId,
                    });
            }

            await transaction.CommitAsync(cancellationToken);
            return new AnalyticsInteractionRegistration(existing.RecordedAtUtc, Replayed: true);
        }

        if (interaction.ListingId is { } listingId)
        {
            await IncrementMetricsAsync(
                connection,
                transaction,
                interaction.CatalogKey,
                listingId,
                interaction.Kind,
                interaction.RecordedAtUtc,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new AnalyticsInteractionRegistration(insertedAt.Value, Replayed: false);
    }

    public async Task<AnalyticsListingMetricsSnapshot?> ReadListingMetricsAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        await using var command = dataSource.CreateCommand("""
            SELECT catalog_key,
                   listing_id,
                   listing_views,
                   contact_clicks,
                   leads,
                   updated_at_utc
            FROM analytics.listing_metric
            WHERE catalog_key = @catalog_key
              AND listing_id = @listing_id;
            """);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, listingId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new AnalyticsListingMetricsSnapshot(
            reader.GetString(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetFieldValue<DateTimeOffset>(5));
    }

    public async Task<bool> CheckReadinessAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1 FROM analytics.listing_metric LIMIT 1;");
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return false;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }

    private static async Task<DateTimeOffset?> TryInsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AnalyticsInteractionRecord interaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO analytics.interaction_event
            (
                event_id,
                request_digest,
                catalog_key,
                public_read_revision_id,
                listing_id,
                session_hash,
                kind,
                occurred_at_utc,
                recorded_at_utc
            )
            VALUES
            (
                @event_id,
                @request_digest,
                @catalog_key,
                @public_read_revision_id,
                @listing_id,
                @session_hash,
                @kind,
                @occurred_at_utc,
                @recorded_at_utc
            )
            ON CONFLICT (event_id) DO NOTHING
            RETURNING recorded_at_utc;
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, interaction.EventId);
        command.Parameters.AddWithValue("request_digest", NpgsqlDbType.Char, interaction.RequestDigest);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, interaction.CatalogKey);
        command.Parameters.AddWithValue(
            "public_read_revision_id",
            NpgsqlDbType.Uuid,
            interaction.PublicReadRevisionId);
        command.Parameters.AddWithValue(
            "listing_id",
            NpgsqlDbType.Uuid,
            interaction.ListingId is { } listingId ? listingId : DBNull.Value);
        command.Parameters.AddWithValue("session_hash", NpgsqlDbType.Char, interaction.SessionHash);
        command.Parameters.AddWithValue("kind", NpgsqlDbType.Integer, (int)interaction.Kind);
        command.Parameters.AddWithValue(
            "occurred_at_utc",
            NpgsqlDbType.TimestampTz,
            interaction.OccurredAtUtc);
        command.Parameters.AddWithValue(
            "recorded_at_utc",
            NpgsqlDbType.TimestampTz,
            interaction.RecordedAtUtc);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : (DateTimeOffset)value;
    }

    private static async Task<(string RequestDigest, DateTimeOffset RecordedAtUtc)> ReadExistingAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            SELECT request_digest, recorded_at_utc
            FROM analytics.interaction_event
            WHERE event_id = @event_id
            FOR SHARE;
            """, connection, transaction);
        command.Parameters.AddWithValue("event_id", NpgsqlDbType.Uuid, eventId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Analytics event '{eventId}' conflicted but could not be read.");
        }

        return (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static async Task IncrementMetricsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        Guid listingId,
        AnalyticsInteractionKindContract kind,
        DateTimeOffset updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var listingViewIncrement = kind == AnalyticsInteractionKindContract.ListingView ? 1 : 0;
        var contactClickIncrement = kind == AnalyticsInteractionKindContract.ContactClick ? 1 : 0;
        var leadIncrement = kind == AnalyticsInteractionKindContract.Lead ? 1 : 0;
        await using var command = new NpgsqlCommand("""
            INSERT INTO analytics.listing_metric
            (
                catalog_key,
                listing_id,
                listing_views,
                contact_clicks,
                leads,
                updated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @listing_id,
                @listing_views,
                @contact_clicks,
                @leads,
                @updated_at_utc
            )
            ON CONFLICT (catalog_key, listing_id)
            DO UPDATE SET
                listing_views = analytics.listing_metric.listing_views + EXCLUDED.listing_views,
                contact_clicks = analytics.listing_metric.contact_clicks + EXCLUDED.contact_clicks,
                leads = analytics.listing_metric.leads + EXCLUDED.leads,
                updated_at_utc = GREATEST(
                    analytics.listing_metric.updated_at_utc,
                    EXCLUDED.updated_at_utc);
            """, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        command.Parameters.AddWithValue("listing_id", NpgsqlDbType.Uuid, listingId);
        command.Parameters.AddWithValue("listing_views", NpgsqlDbType.Bigint, listingViewIncrement);
        command.Parameters.AddWithValue("contact_clicks", NpgsqlDbType.Bigint, contactClickIncrement);
        command.Parameters.AddWithValue("leads", NpgsqlDbType.Bigint, leadIncrement);
        command.Parameters.AddWithValue("updated_at_utc", NpgsqlDbType.TimestampTz, updatedAtUtc);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
