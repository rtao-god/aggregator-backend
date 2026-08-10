using Aggregator.Query.Infrastructure;
using Npgsql;

namespace Query.Infrastructure.Tests;

public sealed class PublicProjectionStatusStoreIntegrationTests
{
    private const string CatalogKey = "berlin-recording-services";
    private static readonly Guid BaseProjectionId =
        Guid.Parse("01990420-0000-7000-8000-000000000001");
    private static readonly Guid PromotionOverlayId =
        Guid.Parse("01990420-0000-7000-8000-000000000002");
    private static readonly Guid SafetyOverlayId =
        Guid.Parse("01990420-0000-7000-8000-000000000003");
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990420-0000-7000-8000-000000000004");
    private static readonly Guid SourcePublicationId =
        Guid.Parse("01990420-0000-7000-8000-000000000005");
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 10, 16, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task StoreReadsPointerCheckpointSitemapAndActiveBlockExactly()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await SeedReadyProjectionAsync(database);
        await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).Build();
        var store = new PostgresPublicProjectionStatusStore(dataSource);

        var ready = await store.ReadAsync(CatalogKey, CancellationToken.None);

        Assert.NotNull(ready);
        Assert.Equal(PublicReadRevisionId, ready!.PublicReadRevision?.Id);
        Assert.Equal(BaseProjectionId, ready.PublicReadRevision?.BaseProjectionId);
        Assert.Equal(SourcePublicationId, ready.PublicReadRevision?.SourcePublicationId);
        Assert.Equal(3, ready.PublicReadActivationRevision);
        Assert.Equal(1, ready.CatalogSourceActivationRevision);
        Assert.Equal(PublicReadRevisionId, ready.CatalogCheckpointPublicReadRevisionId);
        Assert.Equal(BaseProjectionId, ready.CatalogCheckpointBaseProjectionId);
        Assert.Equal(SourcePublicationId, ready.CatalogCheckpointSourcePublicationId);
        Assert.Equal(PublicReadRevisionId, ready.SitemapPublicReadRevisionId);
        Assert.Equal(0, ready.SitemapRecordCount);
        Assert.Equal(0, ready.ActiveReadBlockCount);
        Assert.Null(ready.OldestReadBlockAtUtc);

        var blockedAtUtc = CreatedAtUtc.AddMinutes(5);
        await database.ExecuteAsync(
            """
            INSERT INTO projection.catalog_visibility_block
            (
                block_id,
                catalog_key,
                source_event_id,
                suppression_id,
                suppression_revision,
                payload_digest,
                reason_code,
                blocked_at_utc,
                block_kind
            )
            VALUES
            (
                @block_id,
                @catalog_key,
                @source_event_id,
                NULL,
                NULL,
                @payload_digest,
                'QUERY_PUBLICATION_RECOMPOSITION_PENDING',
                @blocked_at_utc,
                'publication_recomposition'
            );
            """,
            new NpgsqlParameter<Guid>(
                "block_id",
                Guid.Parse("01990420-0000-7000-8000-000000000020")),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<Guid>(
                "source_event_id",
                Guid.Parse("01990420-0000-7000-8000-000000000021")),
            new NpgsqlParameter<string>("payload_digest", new string('f', 64)),
            QueryPostgresTestDatabase.UtcParameter("blocked_at_utc", blockedAtUtc));

        var blocked = await store.ReadAsync(CatalogKey, CancellationToken.None);

        Assert.NotNull(blocked);
        Assert.Equal(1, blocked!.ActiveReadBlockCount);
        Assert.Equal(blockedAtUtc, blocked.OldestReadBlockAtUtc);
        Assert.Equal(PublicReadRevisionId, blocked.PublicReadRevision?.Id);
    }

    [Fact]
    public async Task StoreReturnsNullWhenQueryOwnsNoEvidenceForCatalog()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await using var dataSource = new NpgsqlDataSourceBuilder(database.ConnectionString).Build();
        var store = new PostgresPublicProjectionStatusStore(dataSource);

        var result = await store.ReadAsync("unknown-catalog", CancellationToken.None);

        Assert.Null(result);
    }

    private static Task SeedReadyProjectionAsync(QueryPostgresTestDatabase database) =>
        database.ExecuteAsync(
            """
            BEGIN;

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
                @source_publication_digest,
                1,
                'query-projection-status-test',
                @created_at_utc,
                @base_digest
            );

            INSERT INTO projection.overlay_revision
            (id, catalog_key, kind, source_revision, created_at_utc, content_digest, item_count)
            VALUES
            (@promotion_overlay_id, @catalog_key, 'promotion', 0, @created_at_utc, @promotion_digest, 0),
            (@safety_overlay_id, @catalog_key, 'visibility_safety', 0, @created_at_utc, @safety_digest, 0);

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
                @created_at_utc,
                @public_read_digest
            );

            INSERT INTO projection.current_public_read
            (catalog_key, public_read_revision_id, activation_revision, activated_at_utc)
            VALUES
            (@catalog_key, @public_read_revision_id, 3, @public_read_activated_at_utc);

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
                1,
                @public_read_revision_id,
                @catalog_event_id,
                @catalog_payload_digest,
                @checkpoint_updated_at_utc
            );

            INSERT INTO seo_projection.sitemap_revision
            (catalog_key, public_read_revision_id, content_digest, record_count, built_at_utc)
            VALUES
            (@catalog_key, @public_read_revision_id, @sitemap_digest, 0, @sitemap_built_at_utc);

            INSERT INTO seo_projection.active_sitemap_revision
            (catalog_key, public_read_revision_id, activated_at_utc)
            VALUES
            (@catalog_key, @public_read_revision_id, @sitemap_activated_at_utc);

            COMMIT;
            """,
            new NpgsqlParameter<Guid>("base_projection_id", BaseProjectionId),
            new NpgsqlParameter<Guid>("promotion_overlay_id", PromotionOverlayId),
            new NpgsqlParameter<Guid>("safety_overlay_id", SafetyOverlayId),
            new NpgsqlParameter<Guid>("public_read_revision_id", PublicReadRevisionId),
            new NpgsqlParameter<Guid>("source_publication_id", SourcePublicationId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<string>("source_publication_digest", new string('a', 64)),
            new NpgsqlParameter<string>("base_digest", new string('b', 64)),
            new NpgsqlParameter<string>("promotion_digest", new string('c', 64)),
            new NpgsqlParameter<string>("safety_digest", new string('d', 64)),
            new NpgsqlParameter<string>("public_read_digest", new string('e', 64)),
            new NpgsqlParameter<string>("catalog_payload_digest", new string('1', 64)),
            new NpgsqlParameter<string>("sitemap_digest", new string('2', 64)),
            new NpgsqlParameter<Guid>(
                "catalog_event_id",
                Guid.Parse("01990420-0000-7000-8000-000000000010")),
            QueryPostgresTestDatabase.UtcParameter("created_at_utc", CreatedAtUtc),
            QueryPostgresTestDatabase.UtcParameter(
                "checkpoint_updated_at_utc",
                CreatedAtUtc.AddMinutes(1)),
            QueryPostgresTestDatabase.UtcParameter(
                "public_read_activated_at_utc",
                CreatedAtUtc.AddMinutes(2)),
            QueryPostgresTestDatabase.UtcParameter(
                "sitemap_built_at_utc",
                CreatedAtUtc.AddMinutes(3)),
            QueryPostgresTestDatabase.UtcParameter(
                "sitemap_activated_at_utc",
                CreatedAtUtc.AddMinutes(4)));
}
