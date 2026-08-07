using Npgsql;

namespace Query.Infrastructure.Tests;

public sealed class QueryActivationRevisionMigrationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 17, 0, 0, TimeSpan.Zero);
    private static readonly Guid BaseProjectionId =
        Guid.Parse("01990000-0000-7000-8000-000000000001");
    private static readonly Guid PromotionOverlayId =
        Guid.Parse("01990000-0000-7000-8000-000000000002");
    private static readonly Guid SafetyOverlayId =
        Guid.Parse("01990000-0000-7000-8000-000000000003");
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990000-0000-7000-8000-000000000004");
    private static readonly Guid SourcePublicationId =
        Guid.Parse("01990000-0000-7000-8000-000000000005");
    private static readonly Guid EventId =
        Guid.Parse("01990000-0000-7000-8000-000000000006");
    private const string CatalogKey = "berlin-recording-services";
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task FreshCheckpointMustStartAtActivationRevisionOne()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await SeedProjectionComponentsAsync(database);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            INSERT INTO projection.catalog_activation_checkpoint
            (
                catalog_key,
                last_activation_revision,
                current_public_read_revision_id,
                last_event_id,
                last_payload_digest,
                updated_at_utc
            )
            VALUES
            (
                @catalog_key,
                2,
                @public_read_revision_id,
                @event_id,
                @digest,
                @timestamp
            );
            """,
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            new NpgsqlParameter<Guid>("event_id", EventId),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp)));

        Assert.Equal("P7202", exception.SqlState);
        Assert.Contains(
            "expected activation revision 1",
            exception.Detail ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                "SELECT count(*) FROM projection.catalog_activation_checkpoint;"));
    }

    [Fact]
    public async Task RuntimeGapRollsBackCurrentPointerAndCheckpointTogether()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await SeedProjectionComponentsAsync(database);
        await InsertCheckpointAndPointerAsync(database, activationRevision: 1);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => database.ExecuteAsync(
            """
            BEGIN;

            UPDATE projection.current_public_read
            SET activation_revision = 3,
                activated_at_utc = @timestamp
            WHERE catalog_key = @catalog_key;

            UPDATE projection.catalog_activation_checkpoint
            SET last_activation_revision = 3,
                updated_at_utc = @timestamp
            WHERE catalog_key = @catalog_key;

            COMMIT;
            """,
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp.AddMinutes(1))));

        Assert.Equal("P7202", exception.SqlState);
        Assert.Contains(
            "expected activation revision 2",
            exception.Detail ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT activation_revision
                FROM projection.current_public_read
                WHERE catalog_key = 'berlin-recording-services';
                """));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT last_activation_revision
                FROM projection.catalog_activation_checkpoint
                WHERE catalog_key = 'berlin-recording-services';
                """));
    }

    [Fact]
    public async Task HistoricalGapBlocksMigrationUntilQueryIsRebuilt()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await ApplyPreContiguityMigrationsAsync(database);
        await SeedProjectionComponentsAsync(database);
        await InsertInboxAsync(database, activationRevision: 2);
        await database.ExecuteAsync(
            """
            INSERT INTO projection.catalog_activation_checkpoint
            (
                catalog_key,
                last_activation_revision,
                current_public_read_revision_id,
                last_event_id,
                last_payload_digest,
                updated_at_utc
            )
            VALUES
            (
                @catalog_key,
                2,
                @public_read_revision_id,
                @event_id,
                @digest,
                @timestamp
            );
            """,
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            new NpgsqlParameter<Guid>("event_id", EventId),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            database.ExecuteQueryMigrationAsync(
                "V008__catalog_activation_revision_contiguity.sql"));

        Assert.Equal("P7201", exception.SqlState);
        Assert.Contains(
            "activation revision 1 is absent",
            exception.Detail ?? string.Empty,
            StringComparison.Ordinal);
    }

    private static async Task ApplyPreContiguityMigrationsAsync(
        QueryPostgresTestDatabase database)
    {
        foreach (var migration in new[]
                 {
                     "V001__query_projection_schema.sql",
                     "V002__query_runtime_schema.sql",
                     "V003__promotion_overlay_projection.sql",
                     "V004__promotion_placement_projection.sql",
                     "V005__visibility_safety_projection.sql",
                     "V006__publication_overlay_recomposition.sql",
                     "V007__public_contact_identity.sql",
                 })
        {
            await database.ExecuteQueryMigrationAsync(migration);
        }
    }

    private static Task SeedProjectionComponentsAsync(
        QueryPostgresTestDatabase database) =>
        database.ExecuteAsync(
            """
            INSERT INTO projection.base_projection
            (
                id,
                catalog_key,
                default_locale,
                supported_locales,
                source_publication_id,
                source_publication_digest,
                source_publication_sequence,
                builder_identity,
                created_at_utc,
                content_digest
            )
            VALUES
            (
                @base_projection_id,
                @catalog_key,
                'de-DE',
                ARRAY['de-DE']::text[],
                @source_publication_id,
                @digest,
                1,
                'query-projection-builder@1',
                @timestamp,
                @digest
            );

            INSERT INTO projection.overlay_revision
            (
                id,
                catalog_key,
                kind,
                source_revision,
                created_at_utc,
                content_digest,
                item_count
            )
            VALUES
            (
                @promotion_overlay_id,
                @catalog_key,
                'promotion',
                0,
                @timestamp,
                @digest,
                0
            ),
            (
                @safety_overlay_id,
                @catalog_key,
                'visibility_safety',
                0,
                @timestamp,
                @digest,
                0
            );

            INSERT INTO projection.public_read_revision
            (
                id,
                catalog_key,
                base_projection_id,
                promotion_overlay_id,
                safety_overlay_id,
                source_publication_id,
                created_at_utc,
                content_digest
            )
            VALUES
            (
                @public_read_revision_id,
                @catalog_key,
                @base_projection_id,
                @promotion_overlay_id,
                @safety_overlay_id,
                @source_publication_id,
                @timestamp,
                @digest
            );
            """,
            new NpgsqlParameter<Guid>("base_projection_id", BaseProjectionId),
            new NpgsqlParameter<Guid>("promotion_overlay_id", PromotionOverlayId),
            new NpgsqlParameter<Guid>("safety_overlay_id", SafetyOverlayId),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            new NpgsqlParameter<Guid>("source_publication_id", SourcePublicationId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));

    private static async Task InsertCheckpointAndPointerAsync(
        QueryPostgresTestDatabase database,
        long activationRevision)
    {
        await InsertInboxAsync(database, activationRevision);
        await database.ExecuteAsync(
            """
            INSERT INTO projection.current_public_read
            (
                catalog_key,
                public_read_revision_id,
                activation_revision,
                activated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @public_read_revision_id,
                @activation_revision,
                @timestamp
            );

            INSERT INTO projection.catalog_activation_checkpoint
            (
                catalog_key,
                last_activation_revision,
                current_public_read_revision_id,
                last_event_id,
                last_payload_digest,
                updated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @activation_revision,
                @public_read_revision_id,
                @event_id,
                @digest,
                @timestamp
            );
            """,
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            new NpgsqlParameter<long>("activation_revision", activationRevision),
            new NpgsqlParameter<Guid>("event_id", EventId),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));
    }

    private static Task InsertInboxAsync(
        QueryPostgresTestDatabase database,
        long activationRevision) =>
        database.ExecuteAsync(
            """
            INSERT INTO messaging.inbox_message
            (
                event_id,
                event_type,
                payload_digest,
                catalog_key,
                activation_revision,
                outcome,
                result_public_read_revision_id,
                received_at_utc
            )
            VALUES
            (
                @event_id,
                'catalog.publication.activated',
                @digest,
                @catalog_key,
                @activation_revision,
                'activated',
                @public_read_revision_id,
                @timestamp
            );
            """,
            new NpgsqlParameter<Guid>("event_id", EventId),
            new NpgsqlParameter<string>("digest", Digest),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<long>("activation_revision", activationRevision),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));
}
