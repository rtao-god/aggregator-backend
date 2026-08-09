using System.Data;
using Aggregator.Catalog.Contracts;
using Aggregator.Ingestion.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>PostgreSQL inbox and projection owner for Catalog configuration activations.</summary>
public sealed class PostgresCatalogConfigurationProjectionStore(
    IngestionDbContext dbContext,
    TimeProvider timeProvider) : ICatalogConfigurationProjectionStore
{
    private const long AdvisoryLockSeed = 7_061_903_711;

    public async Task<CatalogConfigurationProjectionResult> ApplyAsync(
        CatalogConfigurationProjection projection,
        CatalogConfigurationInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ValidatePair(projection, inboxMessage);
        var processedAtUtc = RequireUtc(timeProvider.GetUtcNow(), nameof(processedAtUtc));
        if (processedAtUtc < inboxMessage.ReceivedAtUtc)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_CLOCK_REGRESSION",
                500,
                "The projection clock is earlier than the broker receive timestamp.",
                "Correct the Ingestion worker clock before replaying Catalog events.",
                projection);
        }

        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);
            await AcquireCatalogLockAsync(
                connection,
                transaction,
                projection.CatalogKey,
                cancellationToken);

            var existingInbox = await ReadInboxAsync(
                connection,
                transaction,
                inboxMessage.EventId,
                cancellationToken);
            if (existingInbox is not null)
            {
                EnsureReplay(existingInbox, projection, inboxMessage);
                await transaction.CommitAsync(cancellationToken);
                return new CatalogConfigurationProjectionResult(
                    projection,
                    CatalogConfigurationProjectionDisposition.Replayed);
            }

            var current = await ReadCurrentAsync(
                connection,
                transaction,
                projection.CatalogKey,
                cancellationToken);
            ValidateNext(current, projection);
            await InsertInboxAsync(
                connection,
                transaction,
                projection,
                inboxMessage,
                processedAtUtc,
                cancellationToken);
            await UpsertProjectionAsync(
                connection,
                transaction,
                projection,
                inboxMessage,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CatalogConfigurationProjectionResult(
                projection,
                CatalogConfigurationProjectionDisposition.Applied);
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static async Task AcquireCatalogLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@catalog_key, @seed));",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        command.Parameters.Add(new NpgsqlParameter<long>("seed", AdvisoryLockSeed));
        _ = await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<StoredInbox?> ReadInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid messageId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                message_id,
                routing_key,
                contract_identity,
                payload_digest,
                site_key,
                catalog_key,
                configuration_revision_id,
                previous_configuration_revision_id,
                aggregate_revision,
                correlation_id,
                projection_digest
            FROM messaging.catalog_configuration_inbox
            WHERE message_id = @message_id;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("message_id", messageId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredInbox(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10));
    }

    private static async Task<StoredProjection?> ReadCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                site_key,
                catalog_key,
                active_configuration_revision_id,
                configuration_digest,
                market_area_key,
                supported_listing_kinds,
                aggregate_revision,
                source_event_id,
                source_payload_digest,
                activated_at_utc,
                projection_digest
            FROM catalog_projection.catalog_reference
            WHERE catalog_key = @catalog_key
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new StoredProjection(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<int[]>(5),
            reader.GetInt64(6),
            reader.GetGuid(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetString(10));
    }

    private static async Task InsertInboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CatalogConfigurationProjection projection,
        CatalogConfigurationInboxMessage message,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO messaging.catalog_configuration_inbox
            (
                message_id,
                routing_key,
                contract_identity,
                payload_digest,
                site_key,
                catalog_key,
                configuration_revision_id,
                previous_configuration_revision_id,
                aggregate_revision,
                correlation_id,
                received_at_utc,
                processed_at_utc,
                projection_digest
            )
            VALUES
            (
                @message_id,
                @routing_key,
                @contract_identity,
                @payload_digest,
                @site_key,
                @catalog_key,
                @configuration_revision_id,
                @previous_configuration_revision_id,
                @aggregate_revision,
                @correlation_id,
                @received_at_utc,
                @processed_at_utc,
                @projection_digest
            );
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("message_id", message.EventId));
        command.Parameters.Add(new NpgsqlParameter<string>("routing_key", message.RoutingKey));
        command.Parameters.Add(new NpgsqlParameter<string>("contract_identity", message.ContractIdentity));
        command.Parameters.Add(new NpgsqlParameter<string>("payload_digest", message.PayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<string>("site_key", projection.SiteKey));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", projection.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "configuration_revision_id",
            projection.ConfigurationRevisionId));
        command.Parameters.Add(new NpgsqlParameter<Guid?>(
            "previous_configuration_revision_id",
            NpgsqlDbType.Uuid)
        {
            TypedValue = projection.PreviousConfigurationRevisionId,
        });
        command.Parameters.Add(new NpgsqlParameter<long>(
            "aggregate_revision",
            projection.AggregateRevision));
        command.Parameters.Add(new NpgsqlParameter<string>("correlation_id", message.CorrelationId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("received_at_utc", message.ReceivedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("processed_at_utc", processedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("projection_digest", projection.ProjectionDigest));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertProjectionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CatalogConfigurationProjection projection,
        CatalogConfigurationInboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO catalog_projection.catalog_reference
            (
                site_key,
                catalog_key,
                active_configuration_revision_id,
                configuration_digest,
                market_area_key,
                supported_listing_kinds,
                aggregate_revision,
                source_event_id,
                source_payload_digest,
                activated_at_utc,
                projection_digest,
                updated_at_utc
            )
            VALUES
            (
                @site_key,
                @catalog_key,
                @configuration_revision_id,
                @configuration_digest,
                @market_area_key,
                @supported_listing_kinds,
                @aggregate_revision,
                @source_event_id,
                @source_payload_digest,
                @activated_at_utc,
                @projection_digest,
                @updated_at_utc
            )
            ON CONFLICT (site_key, catalog_key)
            DO UPDATE SET
                active_configuration_revision_id = EXCLUDED.active_configuration_revision_id,
                configuration_digest = EXCLUDED.configuration_digest,
                market_area_key = EXCLUDED.market_area_key,
                supported_listing_kinds = EXCLUDED.supported_listing_kinds,
                aggregate_revision = EXCLUDED.aggregate_revision,
                source_event_id = EXCLUDED.source_event_id,
                source_payload_digest = EXCLUDED.source_payload_digest,
                activated_at_utc = EXCLUDED.activated_at_utc,
                projection_digest = EXCLUDED.projection_digest,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<string>("site_key", projection.SiteKey));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", projection.CatalogKey));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "configuration_revision_id",
            projection.ConfigurationRevisionId));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "configuration_digest",
            projection.ConfigurationDigest));
        command.Parameters.Add(new NpgsqlParameter<string>("market_area_key", projection.MarketAreaKey));
        command.Parameters.Add(new NpgsqlParameter<int[]>(
            "supported_listing_kinds",
            projection.SupportedListingKinds.Select(kind => (int)kind).ToArray()));
        command.Parameters.Add(new NpgsqlParameter<long>(
            "aggregate_revision",
            projection.AggregateRevision));
        command.Parameters.Add(new NpgsqlParameter<Guid>("source_event_id", projection.SourceEventId));
        command.Parameters.Add(new NpgsqlParameter<string>(
            "source_payload_digest",
            projection.SourcePayloadDigest));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "activated_at_utc",
            projection.ActivatedAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("projection_digest", projection.ProjectionDigest));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "updated_at_utc",
            message.ReceivedAtUtc));
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidatePair(
        CatalogConfigurationProjection projection,
        CatalogConfigurationInboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(message);
        if (message.EventId != projection.SourceEventId ||
            !string.Equals(message.PayloadDigest, projection.SourcePayloadDigest, StringComparison.Ordinal) ||
            !string.Equals(
                message.RoutingKey,
                CatalogIntegrationEventTypes.ConfigurationActivated,
                StringComparison.Ordinal) ||
            !string.Equals(
                message.ContractIdentity,
                CatalogIntegrationEventContracts.ConfigurationActivated,
                StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_ENVELOPE_MISMATCH",
                422,
                "Catalog configuration projection and inbox envelope do not describe the same producer event.",
                "Correct the worker envelope mapping before replay.",
                projection);
        }

        _ = RequireUtc(message.ReceivedAtUtc, nameof(message.ReceivedAtUtc));
    }

    private static void EnsureReplay(
        StoredInbox existing,
        CatalogConfigurationProjection projection,
        CatalogConfigurationInboxMessage message)
    {
        if (existing.MessageId != message.EventId ||
            !string.Equals(existing.RoutingKey, message.RoutingKey, StringComparison.Ordinal) ||
            !string.Equals(existing.ContractIdentity, message.ContractIdentity, StringComparison.Ordinal) ||
            !string.Equals(existing.PayloadDigest, message.PayloadDigest, StringComparison.Ordinal) ||
            !string.Equals(existing.SiteKey, projection.SiteKey, StringComparison.Ordinal) ||
            !string.Equals(existing.CatalogKey, projection.CatalogKey, StringComparison.Ordinal) ||
            existing.ConfigurationRevisionId != projection.ConfigurationRevisionId ||
            existing.PreviousConfigurationRevisionId != projection.PreviousConfigurationRevisionId ||
            existing.AggregateRevision != projection.AggregateRevision ||
            !string.Equals(existing.CorrelationId, message.CorrelationId, StringComparison.Ordinal) ||
            !string.Equals(existing.ProjectionDigest, projection.ProjectionDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_INBOX_CORRUPT",
                409,
                "A Catalog configuration message ID was reused with different metadata, payload, or projection effects.",
                "Quarantine the divergent message and restore the exact producer event before replay.",
                projection);
        }
    }

    private static void ValidateNext(
        StoredProjection? current,
        CatalogConfigurationProjection incoming)
    {
        if (current is null)
        {
            if (incoming.AggregateRevision != 1 || incoming.PreviousConfigurationRevisionId is not null)
            {
                throw Gap(incoming, expectedRevision: 1, actualRevision: null);
            }

            return;
        }

        if (!string.Equals(current.SiteKey, incoming.SiteKey, StringComparison.Ordinal))
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_SITE_CHANGED",
                409,
                $"Catalog '{incoming.CatalogKey}' moved from site '{current.SiteKey}' to '{incoming.SiteKey}'.",
                "Correct the Catalog owner identity; a catalog cannot change its site through an activation event.",
                incoming);
        }

        var expectedRevision = checked(current.AggregateRevision + 1);
        if (incoming.AggregateRevision > expectedRevision)
        {
            throw Gap(incoming, expectedRevision, current.AggregateRevision);
        }

        if (incoming.AggregateRevision < expectedRevision)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_REVISION_REUSED",
                409,
                $"Catalog configuration aggregate revision '{incoming.AggregateRevision}' was received under a new message identity after revision '{current.AggregateRevision}'.",
                "Replay the exact previously accepted message or rebuild from the complete Catalog activation stream.",
                incoming);
        }

        if (incoming.PreviousConfigurationRevisionId != current.ConfigurationRevisionId)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_POINTER_CHAIN_MISMATCH",
                409,
                "Catalog configuration activation does not continue from the current Ingestion projection pointer.",
                "Replay the missing or corrected Catalog activation stream in aggregate-revision order.",
                incoming);
        }
    }

    private static IngestionApplicationException Gap(
        CatalogConfigurationProjection incoming,
        long expectedRevision,
        long? actualRevision) =>
        new(
            "Ingestion.CatalogProjection",
            "INGESTION_CATALOG_CONFIGURATION_REVISION_GAP",
            503,
            $"Catalog '{incoming.CatalogKey}' expected activation revision '{expectedRevision}' but received '{incoming.AggregateRevision}'.",
            "Replay Catalog configuration activations beginning with the next expected aggregate revision.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = incoming.CatalogKey,
                ["expectedAggregateRevision"] = expectedRevision,
                ["actualProjectedRevision"] = actualRevision,
                ["receivedAggregateRevision"] = incoming.AggregateRevision,
            });

    private static DateTimeOffset RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "INGESTION_CATALOG_CONFIGURATION_TIMESTAMP_NOT_UTC",
                422,
                $"Catalog configuration timestamp '{parameterName}' is not UTC.",
                "Correct the producer or worker clock contract before replay.");
        }

        return value;
    }

    private static IngestionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        CatalogConfigurationProjection? projection = null) =>
        new(
            "Ingestion.CatalogProjection",
            code,
            statusCode,
            detail,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = projection?.CatalogKey,
                ["configurationRevisionId"] = projection?.ConfigurationRevisionId,
                ["aggregateRevision"] = projection?.AggregateRevision,
                ["sourceEventId"] = projection?.SourceEventId,
            });

    private sealed record StoredInbox(
        Guid MessageId,
        string RoutingKey,
        string ContractIdentity,
        string PayloadDigest,
        string SiteKey,
        string CatalogKey,
        Guid ConfigurationRevisionId,
        Guid? PreviousConfigurationRevisionId,
        long AggregateRevision,
        string CorrelationId,
        string ProjectionDigest);

    private sealed record StoredProjection(
        string SiteKey,
        string CatalogKey,
        Guid ConfigurationRevisionId,
        string ConfigurationDigest,
        string MarketAreaKey,
        int[] SupportedListingKinds,
        long AggregateRevision,
        Guid SourceEventId,
        string SourcePayloadDigest,
        DateTimeOffset ActivatedAtUtc,
        string ProjectionDigest);
}
