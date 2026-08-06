using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class PostgresCatalogVisibilitySuppressionRepository
{
    private const string SelectSuppressionSql = """
        SELECT id,
               catalog_key,
               target_kind,
               listing_id,
               target_key,
               public_reason_class,
               private_evidence_reference,
               response_mode,
               starts_at_utc,
               expires_at_utc,
               state,
               revision,
               changed_by_actor_id,
               transition_reason,
               changed_at_utc
        FROM catalog.public_visibility_suppression
        WHERE id = @suppression_id;
        """;

    private async Task<bool> ExistsAsync(
        string sql,
        CatalogKey catalogKey,
        Guid? targetId,
        CancellationToken cancellationToken)
    {
        await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey.Value));
        if (targetId is { } exactTargetId)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("target_id", exactTargetId));
        }

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static Guid ParseTargetId(PublicVisibilitySuppressionTarget target) =>
        Guid.TryParse(target.TargetKey, out var targetId) && targetId != Guid.Empty
            ? targetId
            : throw new CatalogInvariantException(
                $"Suppression target '{target.TargetKey}' is not a non-empty UUID.");

    private static PublicVisibilitySuppression ReadSuppression(System.Data.Common.DbDataReader reader)
    {
        var target = PublicVisibilitySuppressionTarget.Create(
            (PublicVisibilitySuppressionTargetKind)reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3),
            reader.GetString(4));
        return PublicVisibilitySuppression.Restore(
            reader.GetGuid(0),
            CatalogKey.Create(reader.GetString(1)),
            target,
            reader.GetString(5),
            reader.GetString(6),
            (PublicVisibilitySuppressionResponseMode)reader.GetInt32(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            (PublicVisibilitySuppressionState)reader.GetInt32(10),
            reader.GetInt64(11),
            reader.GetGuid(12),
            reader.GetString(13),
            reader.GetFieldValue<DateTimeOffset>(14));
    }

    private static async Task InsertCurrentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicVisibilitySuppression suppression,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO catalog.public_visibility_suppression
            (
                id,
                catalog_key,
                target_kind,
                listing_id,
                target_key,
                public_reason_class,
                private_evidence_reference,
                response_mode,
                starts_at_utc,
                expires_at_utc,
                state,
                revision,
                changed_by_actor_id,
                transition_reason,
                changed_at_utc
            )
            VALUES
            (
                @id,
                @catalog_key,
                @target_kind,
                @listing_id,
                @target_key,
                @public_reason_class,
                @private_evidence_reference,
                @response_mode,
                @starts_at_utc,
                @expires_at_utc,
                @state,
                @revision,
                @changed_by_actor_id,
                @transition_reason,
                @changed_at_utc
            );
            """);
        AddSuppressionParameters(command, suppression);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicVisibilitySuppression suppression,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO catalog.public_visibility_suppression_revision
            (
                suppression_id,
                revision,
                catalog_key,
                target_kind,
                listing_id,
                target_key,
                public_reason_class,
                private_evidence_reference,
                response_mode,
                starts_at_utc,
                expires_at_utc,
                state,
                changed_by_actor_id,
                transition_reason,
                changed_at_utc
            )
            VALUES
            (
                @id,
                @revision,
                @catalog_key,
                @target_kind,
                @listing_id,
                @target_key,
                @public_reason_class,
                @private_evidence_reference,
                @response_mode,
                @starts_at_utc,
                @expires_at_utc,
                @state,
                @changed_by_actor_id,
                @transition_reason,
                @changed_at_utc
            );
            """);
        AddSuppressionParameters(command, suppression);
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CatalogOutboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO catalog.outbox_message
            (
                message_id,
                routing_key,
                contract_identity,
                payload_json,
                payload_digest,
                occurred_at_utc,
                correlation_id,
                causation_id,
                lease_token,
                leased_by,
                lease_expires_at_utc,
                delivery_attempts,
                dispatched_at_utc,
                last_error,
                dead_lettered_at_utc,
                dead_letter_reason
            )
            VALUES
            (
                @message_id,
                @routing_key,
                @contract_identity,
                @payload_json,
                @payload_digest,
                @occurred_at_utc,
                @correlation_id,
                @causation_id,
                NULL,
                NULL,
                NULL,
                0,
                NULL,
                NULL,
                NULL,
                NULL
            );
            """);
        command.Parameters.AddWithValue("message_id", NpgsqlDbType.Uuid, message.Id);
        command.Parameters.AddWithValue("routing_key", NpgsqlDbType.Varchar, message.EventType);
        command.Parameters.AddWithValue("contract_identity", NpgsqlDbType.Varchar, message.ContractIdentity);
        command.Parameters.AddWithValue("payload_json", NpgsqlDbType.Text, message.Payload);
        command.Parameters.AddWithValue("payload_digest", NpgsqlDbType.Char, message.PayloadDigest);
        command.Parameters.AddWithValue("occurred_at_utc", NpgsqlDbType.TimestampTz, message.OccurredAtUtc);
        command.Parameters.AddWithValue("correlation_id", NpgsqlDbType.Varchar, message.CorrelationId);
        command.Parameters.Add(new NpgsqlParameter("causation_id", NpgsqlDbType.Uuid)
        {
            Value = message.CausationId is { } causationId ? causationId : DBNull.Value,
        });
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long?> ReadActualRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid suppressionId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT revision
            FROM catalog.public_visibility_suppression
            WHERE id = @suppression_id;
            """);
        command.Parameters.AddWithValue("suppression_id", NpgsqlDbType.Uuid, suppressionId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null
            ? null
            : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddSuppressionParameters(
        NpgsqlCommand command,
        PublicVisibilitySuppression suppression)
    {
        command.Parameters.AddWithValue("id", NpgsqlDbType.Uuid, suppression.Id);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, suppression.CatalogKey.Value);
        command.Parameters.AddWithValue("target_kind", NpgsqlDbType.Integer, (int)suppression.Target.Kind);
        command.Parameters.Add(new NpgsqlParameter("listing_id", NpgsqlDbType.Uuid)
        {
            Value = suppression.Target.ListingId is { } listingId ? listingId : DBNull.Value,
        });
        command.Parameters.AddWithValue("target_key", NpgsqlDbType.Varchar, suppression.Target.TargetKey);
        command.Parameters.AddWithValue("public_reason_class", NpgsqlDbType.Varchar, suppression.PublicReasonClass);
        command.Parameters.AddWithValue(
            "private_evidence_reference",
            NpgsqlDbType.Varchar,
            suppression.PrivateEvidenceReference);
        command.Parameters.AddWithValue("response_mode", NpgsqlDbType.Integer, (int)suppression.ResponseMode);
        command.Parameters.AddWithValue("starts_at_utc", NpgsqlDbType.TimestampTz, suppression.StartsAtUtc);
        command.Parameters.Add(new NpgsqlParameter("expires_at_utc", NpgsqlDbType.TimestampTz)
        {
            Value = suppression.ExpiresAtUtc is { } expiresAtUtc ? expiresAtUtc : DBNull.Value,
        });
        command.Parameters.AddWithValue("state", NpgsqlDbType.Integer, (int)suppression.State);
        command.Parameters.AddWithValue("revision", NpgsqlDbType.Bigint, suppression.Revision);
        command.Parameters.AddWithValue("changed_by_actor_id", NpgsqlDbType.Uuid, suppression.ChangedByActorId);
        command.Parameters.AddWithValue("transition_reason", NpgsqlDbType.Varchar, suppression.TransitionReason);
        command.Parameters.AddWithValue("changed_at_utc", NpgsqlDbType.TimestampTz, suppression.ChangedAtUtc);
    }

    private static NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql) =>
        new(sql, connection, transaction);
}
