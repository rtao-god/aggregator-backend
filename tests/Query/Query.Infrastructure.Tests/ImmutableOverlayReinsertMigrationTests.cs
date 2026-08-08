using Npgsql;

namespace Query.Infrastructure.Tests;

public sealed class ImmutableOverlayReinsertMigrationTests
{
    private static readonly Guid OverlayId =
        Guid.Parse("01990600-0000-7000-8000-000000000001");
    private static readonly DateTimeOffset CreatedAtUtc =
        new(2026, 8, 8, 2, 0, 0, TimeSpan.Zero);
    private const string CatalogKey = "berlin-recording-services";
    private const string Digest =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ExactImmutableOverlayReinsertIsIdempotent()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await InsertOverlayAsync(database, itemCount: 0);

        await InsertOverlayAsync(database, itemCount: 0);

        Assert.Equal(
            1L,
            await database.ScalarAsync<long>(
                """
                SELECT count(*)
                FROM projection.overlay_revision
                WHERE id = @overlay_id;
                """,
                new NpgsqlParameter<Guid>("overlay_id", OverlayId)));
    }

    [Fact]
    public async Task SameOverlayIdWithDifferentOwnerStateIsRejected()
    {
        await using var database = await QueryPostgresTestDatabase.CreateAsync();
        await database.ApplyAllQueryMigrationsAsync();
        await InsertOverlayAsync(database, itemCount: 0);

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertOverlayAsync(database, itemCount: 1));

        Assert.Equal("P7203", exception.SqlState);
        Assert.Contains(
            "immutable overlay identity was reused",
            exception.MessageText,
            StringComparison.Ordinal);
        Assert.Equal(
            0,
            await database.ScalarAsync<int>(
                """
                SELECT item_count
                FROM projection.overlay_revision
                WHERE id = @overlay_id;
                """,
                new NpgsqlParameter<Guid>("overlay_id", OverlayId)));
    }

    private static Task InsertOverlayAsync(
        QueryPostgresTestDatabase database,
        int itemCount) =>
        database.ExecuteAsync(
            """
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
                @overlay_id,
                @catalog_key,
                'promotion',
                0,
                @created_at_utc,
                @content_digest,
                @item_count
            );
            """,
            new NpgsqlParameter<Guid>("overlay_id", OverlayId),
            new NpgsqlParameter<string>("catalog_key", CatalogKey),
            QueryPostgresTestDatabase.UtcParameter("created_at_utc", CreatedAtUtc),
            new NpgsqlParameter<string>("content_digest", Digest),
            new NpgsqlParameter<int>("item_count", itemCount));
}
