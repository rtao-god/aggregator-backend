using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Aggregator.Query.Infrastructure;
using Npgsql;

namespace Query.Infrastructure.Tests;

public sealed class VisibilitySafetyRecoveryTests
{
    private static readonly DateTimeOffset Timestamp =
        new(2026, 8, 7, 20, 0, 0, TimeSpan.Zero);
    private static readonly Guid BaseProjectionId =
        Guid.Parse("01990300-0000-7000-8000-000000000001");
    private static readonly Guid PromotionOverlayId =
        Guid.Parse("01990300-0000-7000-8000-000000000002");
    private static readonly Guid InitialSafetyOverlayId =
        Guid.Parse("01990300-0000-7000-8000-000000000003");
    private static readonly Guid InitialPublicReadRevisionId =
        Guid.Parse("01990300-0000-7000-8000-000000000004");
    private static readonly Guid SourcePublicationId =
        Guid.Parse("01990300-0000-7000-8000-000000000005");
    private static readonly Guid VisibilityEventId =
        Guid.Parse("01990300-0000-7000-8000-000000000010");
    private static readonly Guid SuppressionId =
        Guid.Parse("01990300-0000-7000-8000-000000000011");
    private static readonly Guid MaterializedSafetyOverlayId =
        Guid.Parse("01990300-0000-7000-8000-000000000020");
    private static readonly Guid MaterializedPublicReadRevisionId =
        Guid.Parse("01990300-0000-7000-8000-000000000021");
    private const string CatalogKey = "berlin-recording-services";
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task PendingBlockSurvivesRestartAndDigestConflictReblocksAfterExactReplay()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        var suppression = CreateSuppression();
        var inbox = new VisibilitySuppressionInboxMessage(
            VisibilityEventId,
            Digest,
            Timestamp);

        await using (var firstDataSource = NpgsqlDataSource.Create(database.ConnectionString))
        {
            var firstStore = new PostgresVisibilitySafetyProjectionStore(
                firstDataSource,
                new QueueQueryIdFactory(
                    MaterializedSafetyOverlayId,
                    MaterializedPublicReadRevisionId),
                new FixedQueryClock(Timestamp.AddMinutes(1)));

            var failure = await Assert.ThrowsAsync<QueryProjectionException>(() =>
                firstStore.ApplyAsync(
                    suppression,
                    inbox,
                    CancellationToken.None));

            Assert.Equal("Query.VisibilitySafety", failure.Owner);
            Assert.Equal("QUERY_VISIBILITY_PUBLIC_READ_MISSING", failure.Code);
            Assert.Equal(503, failure.StatusCode);
        }

        Assert.Equal(1L, await CountBlockAsync(database));
        Assert.Equal("pending", await ReadInboxStateAsync(database));
        Assert.Equal(
            0L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.visibility_safety_overlay_item
                WHERE suppression_id = @suppression_id;
                """,
                new NpgsqlParameter<Guid>("suppression_id", SuppressionId)));

        await SeedCurrentReadAsync(database);
        VisibilitySafetyProjectionResult activation;
        await using (var recoveryDataSource = NpgsqlDataSource.Create(database.ConnectionString))
        {
            var recoveryStore = new PostgresVisibilitySafetyProjectionStore(
                recoveryDataSource,
                new QueueQueryIdFactory(
                    MaterializedSafetyOverlayId,
                    MaterializedPublicReadRevisionId),
                new FixedQueryClock(Timestamp.AddMinutes(2)));

            activation = await recoveryStore.ApplyAsync(
                suppression,
                inbox,
                CancellationToken.None);
        }

        Assert.Equal(VisibilitySafetyProjectionDisposition.Activated, activation.Disposition);
        Assert.Equal(MaterializedPublicReadRevisionId, activation.PublicReadRevision.Id);
        Assert.Equal(MaterializedSafetyOverlayId, activation.PublicReadRevision.SafetyOverlayId);
        Assert.Equal(0L, await CountBlockAsync(database));
        Assert.Equal("completed", await ReadInboxStateAsync(database));
        Assert.Equal(
            MaterializedPublicReadRevisionId.ToString("D"),
            await ReadCurrentRevisionIdAsync(database));
        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.visibility_safety_overlay_item
                WHERE overlay_id = @overlay_id
                  AND suppression_id = @suppression_id;
                """,
                new NpgsqlParameter<Guid>("overlay_id", MaterializedSafetyOverlayId),
                new NpgsqlParameter<Guid>("suppression_id", SuppressionId)));

        await using (var replayDataSource = NpgsqlDataSource.Create(database.ConnectionString))
        {
            var replayStore = new PostgresVisibilitySafetyProjectionStore(
                replayDataSource,
                new UnexpectedIdFactory(),
                new FixedQueryClock(Timestamp.AddMinutes(3)));

            var replay = await replayStore.ApplyAsync(
                suppression,
                inbox,
                CancellationToken.None);

            Assert.Equal(VisibilitySafetyProjectionDisposition.Replayed, replay.Disposition);
            Assert.Equal(MaterializedPublicReadRevisionId, replay.PublicReadRevision.Id);
            Assert.Equal(MaterializedSafetyOverlayId, replay.PublicReadRevision.SafetyOverlayId);

            var publicStore = new SafetyAwarePublicQueryStore(
                new NpgsqlPublicQueryStore(replayDataSource),
                replayDataSource,
                new FixedQueryClock(Timestamp.AddMinutes(3)));
            var finalPage = Assert.IsType<PublicReadPageSnapshot>(
                await publicStore.ReadPageAsync(
                    CatalogKey,
                    afterListingId: null,
                    maximumDocuments: 10,
                    categoryKey: null,
                    requestedLocale: "de-DE",
                    Timestamp.AddMinutes(3),
                    CancellationToken.None));
            Assert.Equal(MaterializedPublicReadRevisionId, finalPage.Revision.Id);
            Assert.Equal(MaterializedSafetyOverlayId, finalPage.Revision.SafetyOverlayId);

            var conflict = await Assert.ThrowsAsync<QueryProjectionException>(() =>
                replayStore.ApplyAsync(
                    suppression,
                    inbox with
                    {
                        PayloadDigest =
                            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    },
                    CancellationToken.None));

            Assert.Equal("Query.VisibilitySafety", conflict.Owner);
            Assert.Equal("QUERY_VISIBILITY_REVISION_CONFLICT", conflict.Code);
            Assert.Equal(409, conflict.StatusCode);
            Assert.Contains("digest", conflict.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1L, await CountBlockAsync(database));

            var blocked = await Assert.ThrowsAsync<QueryReadException>(() =>
                publicStore.ReadPageAsync(
                    CatalogKey,
                    afterListingId: null,
                    maximumDocuments: 10,
                    categoryKey: null,
                    requestedLocale: "de-DE",
                    Timestamp.AddMinutes(3),
                    CancellationToken.None));
            Assert.Equal("Query.VisibilitySafety", blocked.Owner);
            Assert.Equal("QUERY_VISIBILITY_UPDATE_PENDING", blocked.Code);
            Assert.Equal(503, blocked.StatusCode);
            Assert.Equal(
                VisibilityEventId,
                Assert.IsType<Guid>(blocked.Context["sourceEventId"]));
        }

        Assert.Equal(1L, await CountBlockAsync(database));
        Assert.Equal("completed", await ReadInboxStateAsync(database));
        Assert.Equal(
            MaterializedPublicReadRevisionId.ToString("D"),
            await ReadCurrentRevisionIdAsync(database));
    }

    private static QueryVisibilitySuppression CreateSuppression() =>
        QueryVisibilitySuppression.Create(
            SuppressionId,
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

    private static Task<long> CountBlockAsync(QueryPostgresTestDatabase database) =>
        database.ScalarAsync<long>(
            """
            SELECT count(*)
            FROM projection.catalog_visibility_block
            WHERE source_event_id = @event_id
              AND catalog_key = @catalog_key;
            """,
            new NpgsqlParameter<Guid>("event_id", VisibilityEventId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey));

    private static Task<string> ReadInboxStateAsync(
        QueryPostgresTestDatabase database) =>
        database.ScalarAsync<string>(
            """
            SELECT processing_state
            FROM messaging.visibility_suppression_inbox_message
            WHERE event_id = @event_id;
            """,
            new NpgsqlParameter<Guid>("event_id", VisibilityEventId));

    private static Task<string> ReadCurrentRevisionIdAsync(
        QueryPostgresTestDatabase database) =>
        database.ScalarAsync<string>(
            """
            SELECT public_read_revision_id::text
            FROM projection.current_public_read
            WHERE catalog_key = @catalog_key;
            """,
            new NpgsqlParameter<string>("catalog_key", CatalogKey));

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
            new NpgsqlParameter<Guid>("safety_overlay_id", InitialSafetyOverlayId),
            new NpgsqlParameter<Guid>("public_read_revision_id", InitialPublicReadRevisionId),
            new NpgsqlParameter<Guid>("source_publication_id", SourcePublicationId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            new NpgsqlParameter<string>("digest", Digest),
            QueryPostgresTestDatabase.UtcParameter("timestamp", Timestamp));

    private sealed class QueueQueryIdFactory(params Guid[] values) : IQueryIdFactory
    {
        private readonly Queue<Guid> _values = new(values);

        public Guid Create() =>
            _values.Count > 0
                ? _values.Dequeue()
                : throw new InvalidOperationException(
                    "Query visibility recovery ID sequence is exhausted.");
    }

    private sealed class UnexpectedIdFactory : IQueryIdFactory
    {
        public Guid Create() =>
            throw new InvalidOperationException(
                "Exact visibility replay must not allocate a new Query identity.");
    }

    private sealed class FixedQueryClock(DateTimeOffset timestamp) : IQueryClock
    {
        public DateTimeOffset GetUtcNow() => timestamp;
    }
}
