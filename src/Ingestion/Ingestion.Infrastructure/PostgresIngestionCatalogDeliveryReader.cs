using System.Data;
using Aggregator.Ingestion.Application;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Reads the Ingestion-owned Catalog delivery ledger without exposing lease credentials.</summary>
public sealed class PostgresIngestionCatalogDeliveryReader(IngestionProcessingDbContext dbContext)
    : IIngestionCatalogDeliveryReader
{
    public async Task<IngestionCatalogDeliveryCollection?> ReadAsync(
        Guid batchId,
        CancellationToken cancellationToken)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("Batch ID is required.", nameof(batchId));
        }

        var connection = dbContext.Database.GetDbConnection() as NpgsqlConnection
            ?? throw Failure(
                "INGESTION_DELIVERY_DATABASE_PROVIDER_INVALID",
                "The Ingestion delivery reader requires an Npgsql connection.",
                "Correct the Ingestion persistence composition root.");
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT b.id,
                       d.delivery_id,
                       d.item_key,
                       d.command_type,
                       d.command_digest,
                       d.state,
                       d.attempt_count,
                       d.lease_expires_at_utc,
                       d.next_attempt_at_utc,
                       d.catalog_listing_id,
                       d.catalog_listing_revision_id,
                       d.failure_code,
                       d.failure_detail,
                       d.created_at_utc,
                       d.last_changed_at_utc
                FROM batches.import_batch b
                LEFT JOIN processing.catalog_delivery d ON d.batch_id = b.id
                WHERE b.id = @batch_id
                ORDER BY d.created_at_utc NULLS FIRST, d.delivery_id NULLS FIRST;
                """,
                connection);
            command.Parameters.Add(new NpgsqlParameter<Guid>("batch_id", batchId));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var foundBatch = false;
            var deliveries = new List<IngestionCatalogDeliverySnapshot>();
            while (await reader.ReadAsync(cancellationToken))
            {
                foundBatch = true;
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                deliveries.Add(IngestionCatalogDeliverySnapshot.Create(
                    reader.GetGuid(1),
                    reader.GetGuid(0),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    (IngestionCatalogDeliveryState)reader.GetInt32(5),
                    reader.GetInt32(6),
                    ReadTimestamp(reader, 7),
                    ReadTimestamp(reader, 8),
                    ReadGuid(reader, 9),
                    ReadGuid(reader, 10),
                    ReadString(reader, 11),
                    ReadString(reader, 12),
                    reader.GetFieldValue<DateTimeOffset>(13),
                    reader.GetFieldValue<DateTimeOffset>(14)));
            }

            return foundBatch
                ? new IngestionCatalogDeliveryCollection(batchId, deliveries)
                : null;
        }
        finally
        {
            if (shouldClose)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private static Guid? ReadGuid(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetGuid(ordinal);

    private static string? ReadString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? ReadTimestamp(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static IngestionApplicationException Failure(
        string code,
        string detail,
        string requiredAction) =>
        new("Ingestion.Delivery", code, 500, detail, requiredAction);
}
