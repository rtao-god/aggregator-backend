using Npgsql;

namespace Query.Infrastructure.Tests;

public sealed class QueryActivationCheckpointUpsertTests
{
    private static readonly Guid BaseProjectionId =
        Guid.Parse("01990700-0000-7000-8000-000000000001");
    private static readonly Guid PromotionOverlayId =
        Guid.Parse("01990700-0000-7000-8000-000000000002");
    private static readonly Guid SafetyOverlayId =
        Guid.Parse("01990700-0000-7000-8000-000000000003");
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990700-0000-7000-8000-000000000004");
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 8, 2, 10, 0, TimeSpan.Zero);
    private const string CatalogKey = "berlin-recording-services";
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task UpsertAdvancesOneRevisionAndRejectsForwardGap()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await SeedProjectionAsync(database);

        await UpsertCheckpointAsync(database, revision: 1, eventSuffix: 10);
        await UpsertCheckpointAsync(database, revision: 2, eventSuffix: 11);

        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                """
                SELECT last_activation_revision
                FROM projection.catalog_activation_checkpoint
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", CatalogKey)));

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            UpsertCheckpointAsync(database, revision: 4, eventSuffix: 12));

        Assert.Equal("P7202", exception.SqlState);
        Assert.Contains(
            "expected activation revision 3",
            exception.Detail ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Equal(
            2L,
            await database.ScalarAsync<long>(
                """
                SELECT last_activation_revision
                FROM projection.catalog_activation_checkpoint
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", CatalogKey)));
    }

    private static Task UpsertCheckpointAsync(
        QueryPostgresTestDatabase database,
        long revision,
        int eventSuffix) =>
        database.ExecuteAsync(
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
                @revision,
                @public_read_revision_id,
                @event_id,
                @digest,
                @timestamp
            )
            ON CONFLICT (catalog_key)
            DO UPDATE SET
                last_activation_revision = EXCLUDED.last_activation_revision,
                current_public_read_revision_id = EXCLUDED.current_public_read_revision_id,
                last_event_id = EXCLUDED.last_event_id,
                last_payload_digest = EXCLUDED.last_payload_digest,
                updated_at_utc = EXCLUDED.updated_at_utc;
            """,
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<long>("revision", revision),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            new NpgsqlParameter<Guid>(
                "event_id",
                Guid.Parse($"01990700-0000-7000-8000-{eventSuffix:D12}")),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter(
                "timestamp",
                Timestamp.AddMinutes(revision)));

    private static Task SeedProjectionAsync(QueryPostgresTestDatabase database) =>
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
                '01990700-0000-7000-8000-000000000005',
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
                '01990700-0000-7000-8000-000000000005',
                @timestamp,
                @digest
            );
            """,
            new NpgsqlParameter<Guid>("base_projection_id", BaseProjectionId),
            new NpgsqlParameter<Guid>("promotion_overlay_id", PromotionOverlayId),
            new NpgsqlParameter<Guid>("safety_overlay_id", SafetyOverlayId),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));
}
