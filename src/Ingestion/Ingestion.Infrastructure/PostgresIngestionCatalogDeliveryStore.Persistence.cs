using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Ingestion.Infrastructure;

public sealed partial class PostgresIngestionCatalogDeliveryStore
{
    private async Task<DeliveryState> ReadForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid deliveryId,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT delivery_id, batch_id, item_key, state, lease_token, lease_expires_at_utc,
                   catalog_listing_id, catalog_listing_revision_id, failure_code, failure_detail
            FROM processing.catalog_delivery
            WHERE delivery_id = @delivery_id
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid>("delivery_id", deliveryId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw Failure(
                "INGESTION_DELIVERY_NOT_FOUND",
                404,
                $"Catalog delivery '{deliveryId:D}' was not found.",
                "Use the exact delivery identity emitted by Ingestion.");
        }

        return new DeliveryState(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.IsDBNull(7) ? null : reader.GetGuid(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));
    }

    private async Task FinalizeBatchIfTerminalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid batchId,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
    {
        long total;
        long delivered;
        long failed;
        await using (var counts = new NpgsqlCommand(
            """
            SELECT count(*),
                   count(*) FILTER (WHERE state = 3),
                   count(*) FILTER (WHERE state = 4)
            FROM processing.catalog_delivery
            WHERE batch_id = @batch_id;
            """,
            connection,
            transaction))
        {
            counts.Parameters.Add(new NpgsqlParameter<Guid>("batch_id", batchId));
            await using var reader = await counts.ExecuteReaderAsync(cancellationToken);
            _ = await reader.ReadAsync(cancellationToken);
            total = reader.GetInt64(0);
            delivered = reader.GetInt64(1);
            failed = reader.GetInt64(2);
        }

        if (total == 0 || delivered + failed != total)
        {
            return;
        }

        await using var update = new NpgsqlCommand(
            """
            UPDATE batches.import_batch
            SET state = CASE WHEN rejected_item_count + @failed = 0 THEN @committed ELSE @partially_rejected END,
                aggregate_revision = aggregate_revision + 1,
                accepted_item_count = @delivered,
                rejected_item_count = rejected_item_count + @failed,
                last_changed_at_utc = @now
            WHERE id = @batch_id
              AND state = @committing;
            """,
            connection,
            transaction);
        update.Parameters.Add(new NpgsqlParameter<int>("failed", checked((int)failed)));
        update.Parameters.Add(new NpgsqlParameter<int>("committed", (int)ImportBatchState.Committed));
        update.Parameters.Add(new NpgsqlParameter<int>("partially_rejected", (int)ImportBatchState.PartiallyRejected));
        update.Parameters.Add(new NpgsqlParameter<int>("delivered", checked((int)delivered)));
        update.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("now", changedAtUtc));
        update.Parameters.Add(new NpgsqlParameter<Guid>("batch_id", batchId));
        update.Parameters.Add(new NpgsqlParameter<int>("committing", (int)ImportBatchState.Committing));
        _ = await update.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IngestionProcessingSnapshot> RequireSnapshotAsync(
        Guid batchId,
        CancellationToken cancellationToken) =>
        await _processingStore.ReadAsync(batchId, cancellationToken)
        ?? throw Failure(
            "INGESTION_BATCH_NOT_FOUND",
            404,
            $"Import batch '{batchId:D}' was not found after Catalog delivery mutation.",
            "Inspect the Ingestion database transaction and restore the missing batch owner state.");

    private NpgsqlConnection RequireConnection() =>
        _dbContext.Database.GetDbConnection() as NpgsqlConnection
        ?? throw Failure(
            "INGESTION_DELIVERY_DATABASE_PROVIDER_INVALID",
            500,
            "The Ingestion delivery store requires an Npgsql connection.",
            "Correct the Ingestion persistence composition root.");

    private static NpgsqlTransaction RequireTransaction(IDbContextTransaction transaction) =>
        transaction.GetDbTransaction() as NpgsqlTransaction
        ?? throw Failure(
            "INGESTION_DELIVERY_TRANSACTION_PROVIDER_INVALID",
            500,
            "The Ingestion delivery store requires an Npgsql transaction.",
            "Correct the Ingestion persistence composition root.");

    private static void AddNullable(NpgsqlCommand command, string name, Guid? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Uuid)
        {
            Value = value is null ? DBNull.Value : value.Value,
        });

    private static void AddNullable(NpgsqlCommand command, string name, string? value) =>
        command.Parameters.Add(new NpgsqlParameter(name, NpgsqlDbType.Text)
        {
            Value = value is null ? DBNull.Value : value,
        });

    private static void EnsureLeaseMutation(int affected, Guid deliveryId)
    {
        if (affected != 1)
        {
            throw new IngestionCatalogDeliveryLeaseLostException(deliveryId);
        }
    }

    private static void RequireUtc(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must be normalized to UTC.", parameterName);
        }
    }

    private static IngestionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction) =>
        new("Ingestion.Delivery", code, statusCode, detail, requiredAction);
}
