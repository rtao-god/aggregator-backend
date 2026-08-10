using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Aggregator.Query.Infrastructure;
using Npgsql;

namespace Query.Infrastructure.Tests;

public sealed class VisibilityBlockIsolationTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid BaseProjectionId =
        Guid.Parse("01990100-0000-7000-8000-000000000001");
    private static readonly Guid PromotionOverlayId =
        Guid.Parse("01990100-0000-7000-8000-000000000002");
    private static readonly Guid SafetyOverlayId =
        Guid.Parse("01990100-0000-7000-8000-000000000003");
    private static readonly Guid PublicReadRevisionId =
        Guid.Parse("01990100-0000-7000-8000-000000000004");
    private static readonly Guid SourcePublicationId =
        Guid.Parse("01990100-0000-7000-8000-000000000005");
    private static readonly Guid AppliedSuppressionId =
        Guid.Parse("01990100-0000-7000-8000-000000000010");
    private static readonly Guid AppliedEventId =
        Guid.Parse("01990100-0000-7000-8000-000000000011");
    private static readonly Guid PendingSuppressionId =
        Guid.Parse("01990100-0000-7000-8000-000000000012");
    private static readonly Guid PendingEventId =
        Guid.Parse("01990100-0000-7000-8000-000000000013");
    private const string CatalogKey = "berlin-recording-services";
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task CompletingOneSuppressionRemovesOnlyItsOwnEventBlock()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await SeedCurrentReadAsync(database);
        await InsertPendingForeignBlockAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        var store = CreateProjectionStore(dataSource);

        var result = await ApplyRouteSuppressionAsync(store);

        Assert.Equal(VisibilitySafetyProjectionDisposition.Activated, result.Disposition);
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.catalog_visibility_block
                WHERE source_event_id = @event_id;
                """,
                new NpgsqlParameter<Guid>("event_id", AppliedEventId)));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.catalog_visibility_block
                WHERE source_event_id = @event_id
                  AND suppression_id = @suppression_id;
                """,
                new NpgsqlParameter<Guid>("event_id", PendingEventId),
                new NpgsqlParameter<Guid>("suppression_id", PendingSuppressionId)));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.catalog_visibility_block
                WHERE catalog_key = @catalog_key;
                """,
                new NpgsqlParameter<string>("catalog_key", CatalogKey)));
    }

    [Fact]
    public async Task RemainingForeignBlockKeepsPublicReadsUnavailable()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await SeedCurrentReadAsync(database);
        await InsertPendingForeignBlockAsync(database);
        await using var dataSource = NpgsqlDataSource.Create(database.ConnectionString);
        _ = await ApplyRouteSuppressionAsync(CreateProjectionStore(dataSource));
        var publicStore = new SafetyAwarePublicQueryStore(
            new NpgsqlPublicQueryStore(dataSource),
            dataSource,
            new FixedQueryClock(Timestamp.AddMinutes(2)));

        var exception = await Assert.ThrowsAsync<QueryReadException>(() => publicStore.ReadPageAsync(
            CatalogKey,
            afterListingId: null,
            maximumDocuments: 20,
            criteria: new PublicListingSearchCriteria(
                "de-DE",
                CategoryKey: null,
                DistrictKey: null,
                ListingKind: null,
                ContactKind: null),
            readAtUtc: Timestamp.AddMinutes(2),
            CancellationToken.None));

        Assert.Equal("Query.VisibilitySafety", exception.Owner);
        Assert.Equal("QUERY_VISIBILITY_UPDATE_PENDING", exception.Code);
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(
            PendingEventId,
            Assert.IsType<Guid>(exception.Context["sourceEventId"]));
        Assert.Equal(
            "catalog_visibility_suppression_pending",
            Assert.IsType<string>(exception.Context["reasonCode"]));
    }

    private static PostgresVisibilitySafetyProjectionStore CreateProjectionStore(
        NpgsqlDataSource dataSource) =>
        new(
            dataSource,
            new UuidV7TestIdFactory(),
            new FixedQueryClock(Timestamp.AddMinutes(1)));

    private static Task<VisibilitySafetyProjectionResult> ApplyRouteSuppressionAsync(
        PostgresVisibilitySafetyProjectionStore store)
    {
        var suppression = QueryVisibilitySuppression.Create(
            AppliedSuppressionId,
            CatalogKey,
            QueryVisibilitySuppressionTargetKind.Route,
            listingId: null,
            "/legal-removal",
            "legal-removal",
            QueryVisibilitySuppressionResponseMode.Gone,
            QueryVisibilitySuppressionState.Active,
            Timestamp,
            expiresAtUtc: null,
            aggregateRevision: 2,
            Timestamp);
        return store.ApplyAsync(
            suppression,
            new VisibilitySuppressionInboxMessage(
                AppliedEventId,
                Digest,
                Timestamp),
            CancellationToken.None);
    }

    private static Task SeedCurrentReadAsync(QueryPostgresTestDatabase database) =>
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
                1,
                @timestamp
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

    private static Task InsertPendingForeignBlockAsync(
        QueryPostgresTestDatabase database) =>
        database.ExecuteAsync(
            """
            INSERT INTO messaging.visibility_suppression_inbox_message
            (
                event_id,
                payload_digest,
                catalog_key,
                suppression_id,
                suppression_revision,
                processing_state,
                result_public_read_revision_id,
                received_at_utc,
                processed_at_utc
            )
            VALUES
            (
                @event_id,
                @digest,
                @catalog_key,
                @suppression_id,
                2,
                'pending',
                NULL,
                @timestamp,
                NULL
            );

            INSERT INTO projection.catalog_visibility_block
            (
                block_id,
                catalog_key,
                source_event_id,
                suppression_id,
                suppression_revision,
                payload_digest,
                reason_code,
                blocked_at_utc
            )
            VALUES
            (
                @event_id,
                @catalog_key,
                @event_id,
                @suppression_id,
                2,
                @digest,
                'catalog_visibility_suppression_pending',
                @timestamp
            );
            """,
            new NpgsqlParameter<Guid>("event_id", PendingEventId),
            new NpgsqlParameter<Guid>("suppression_id", PendingSuppressionId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));

    private sealed class UuidV7TestIdFactory : IQueryIdFactory
    {
        public Guid Create() => Guid.CreateVersion7();
    }

    private sealed class FixedQueryClock(DateTimeOffset timestamp) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => timestamp;
    }
}
