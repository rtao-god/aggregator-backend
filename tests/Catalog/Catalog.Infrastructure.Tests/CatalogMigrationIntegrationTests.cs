using Npgsql;
using NpgsqlTypes;

namespace Catalog.Infrastructure.Tests;

public sealed class CatalogMigrationIntegrationTests
{
    private static readonly string[] ExactOutboxColumns =
    [
        "message_id",
        "routing_key",
        "contract_identity",
        "payload_json",
        "payload_digest",
        "occurred_at_utc",
        "correlation_id",
        "causation_id",
        "lease_token",
        "leased_by",
        "lease_expires_at_utc",
        "delivery_attempts",
        "dispatched_at_utc",
        "last_error",
        "dead_lettered_at_utc",
        "dead_letter_reason",
    ];

    [Fact]
    public async Task FreshCatalogMigrationsCreateExactPayloadOutboxesSuppressionAndMediaSchema()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();

        await database.ApplyAllCatalogMigrationsAsync();

        Assert.Equal(
            ExactOutboxColumns,
            await database.ReadColumnNamesAsync("catalog", "outbox_message"));
        Assert.Equal(
            ExactOutboxColumns,
            await database.ReadColumnNamesAsync("media_messaging", "outbox_message"));
        Assert.Equal(
            "text",
            await database.ScalarAsync<string>(
                """
                SELECT data_type
                FROM information_schema.columns
                WHERE table_schema = 'catalog'
                  AND table_name = 'outbox_message'
                  AND column_name = 'payload_json';
                """));
        Assert.Equal(
            "text",
            await database.ScalarAsync<string>(
                """
                SELECT data_type
                FROM information_schema.columns
                WHERE table_schema = 'media_messaging'
                  AND table_name = 'outbox_message'
                  AND column_name = 'payload_json';
                """));
        Assert.Equal(
            1,
            await database.ScalarAsync<int>(
                """
                SELECT count(*)
                FROM information_schema.tables
                WHERE table_schema = 'catalog'
                  AND table_name = 'public_visibility_suppression';
                """));
        Assert.Equal(
            1,
            await database.ScalarAsync<int>(
                """
                SELECT count(*)
                FROM information_schema.tables
                WHERE table_schema = 'catalog'
                  AND table_name = 'public_visibility_suppression_revision';
                """));
        Assert.Equal(
            5,
            await database.ScalarAsync<int>(
                """
                SELECT count(*)
                FROM information_schema.tables
                WHERE (table_schema, table_name) IN
                (
                    ('media', 'asset'),
                    ('media', 'variant'),
                    ('operations', 'media_command_result'),
                    ('operations', 'processing_work'),
                    ('media_messaging', 'outbox_message')
                );
                """));
        Assert.Equal(
            1,
            await database.ScalarAsync<int>(
                """
                SELECT count(*)
                FROM pg_constraint
                WHERE conname = 'fk_catalog_listing_media_variant'
                  AND conrelid = 'catalog.media'::regclass
                  AND contype = 'f';
                """));
        Assert.Equal(
            1,
            await database.ScalarAsync<int>(
                """
                SELECT count(*)
                FROM pg_trigger
                WHERE tgname = 'tr_catalog_publication_media_safe'
                  AND tgrelid = 'catalog.publication_entry'::regclass
                  AND NOT tgisinternal;
                """));

        await InsertValidOutboxMessageAsync(database);
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            UPDATE catalog.outbox_message
            SET leased_by = 'catalog-integration-test'
            WHERE message_id = '0192f5f0-0000-7000-8000-000000000001';
            """));
        await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            UPDATE catalog.outbox_message
            SET dead_lettered_at_utc = '2026-01-01T00:00:00Z',
                dead_letter_reason = NULL
            WHERE message_id = '0192f5f0-0000-7000-8000-000000000001';
            """));
    }

    [Fact]
    public async Task PartialLegacyMediaOwnerSchemaBlocksCatalogOwnershipTransfer()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await ApplyPreMediaCatalogMigrationsAsync(database);
        await database.ExecuteAsync(
            """
            CREATE SCHEMA media;
            CREATE TABLE media.asset (id uuid PRIMARY KEY);
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteCatalogMigrationAsync("V008__catalog_media_owner_merge.sql"));

        Assert.Contains(
            "blocked by a partial legacy schema",
            exception.MessageText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LegacyRowsBlockUnderdeterminedDurableOutboxUpgrade()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ExecuteCatalogMigrationAsync("V001__catalog_owner_schema.sql");
        await database.ExecuteAsync(
            """
            INSERT INTO catalog.outbox_message
            (
                id,
                event_type,
                event_revision,
                payload,
                occurred_at_utc
            )
            VALUES
            (
                '0192f5f0-0000-7000-8000-000000000001',
                'catalog.publication.activated',
                1,
                '{}',
                '2026-01-01T00:00:00Z'
            );
            """);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteCatalogMigrationAsync("V002__catalog_durable_outbox.sql"));

        Assert.Contains(
            "legacy rows lack canonical payload digests and correlation metadata",
            exception.MessageText,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonbRowsBlockLossyExactPayloadStorageUpgrade()
    {
        await using var database = await CatalogPostgresTestDatabase.CreateAsync();
        await database.ExecuteCatalogMigrationAsync("V001__catalog_owner_schema.sql");
        await database.ExecuteCatalogMigrationAsync("V002__catalog_durable_outbox.sql");
        await InsertValidOutboxMessageAsync(database);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteCatalogMigrationAsync("V006__catalog_outbox_exact_payload_text.sql"));

        Assert.Contains(
            "existing jsonb rows cannot prove their original UTF-8 payload bytes",
            exception.MessageText,
            StringComparison.Ordinal);
    }

    private static async Task ApplyPreMediaCatalogMigrationsAsync(
        CatalogPostgresTestDatabase database)
    {
        foreach (var migration in new[]
                 {
                     "V001__catalog_owner_schema.sql",
                     "V002__catalog_durable_outbox.sql",
                     "V002__catalog_ingestion_drafts.sql",
                     "V003__catalog_publication_activation_revision.sql",
                     "V004__catalog_visibility_suppression.sql",
                     "V005__catalog_contact_identity_contract.sql",
                     "V006__catalog_outbox_exact_payload_text.sql",
                     "V007__catalog_outbox_dead_letter_shape.sql",
                 })
        {
            await database.ExecuteCatalogMigrationAsync(migration);
        }
    }

    private static Task InsertValidOutboxMessageAsync(CatalogPostgresTestDatabase database) =>
        database.ExecuteAsync(
            """
            INSERT INTO catalog.outbox_message
            (
                message_id,
                routing_key,
                contract_identity,
                payload_json,
                payload_digest,
                occurred_at_utc,
                correlation_id,
                causation_id
            )
            VALUES
            (
                @message_id,
                'catalog.publication.activated',
                'aggregator.catalog.publication-activated@1',
                '{}',
                @payload_digest,
                @occurred_at_utc,
                'corr.catalog-migration:0001',
                NULL
            );
            """,
            new NpgsqlParameter<Guid>(
                "message_id",
                Guid.Parse("0192f5f0-0000-7000-8000-000000000001")),
            new NpgsqlParameter<string>(
                "payload_digest",
                "44136fa355b3678a1146ad16f7e8649e94fb4fc21fe77e8310c060f61caaff8a"),
            new NpgsqlParameter("occurred_at_utc", NpgsqlDbType.TimestampTz)
            {
                Value = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            });
}
