using System.Data;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

/// <summary>Read-only Query sitemap adapter over the exact active immutable revision.</summary>
public sealed class PostgresPublicSitemapStore(NpgsqlDataSource dataSource)
    : IPublicSitemapStore
{
    public async Task<PublicSitemapSlice?> ReadPageAsync(
        PublicSitemapPageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var activeRevisionId = await ReadActiveRevisionIdAsync(
            connection,
            transaction,
            request.CatalogKey.Value,
            cancellationToken);
        if (activeRevisionId is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        if (request.Cursor is not null &&
            request.Cursor.PublicReadRevisionId != activeRevisionId.Value)
        {
            await transaction.CommitAsync(cancellationToken);
            return new PublicSitemapSlice(
                activeRevisionId.Value,
                Array.Empty<QuerySitemapDocument>(),
                NextCursor: null);
        }

        var rows = await ReadRecordRowsAsync(
            connection,
            transaction,
            request,
            activeRevisionId.Value,
            cancellationToken);
        var hasMore = rows.Count > request.PageSize;
        var selectedRows = rows
            .Take(request.PageSize)
            .ToArray();
        var hreflang = await ReadHreflangAsync(
            connection,
            transaction,
            request.CatalogKey.Value,
            activeRevisionId.Value,
            selectedRows,
            cancellationToken);
        var documents = selectedRows
            .Select(row => Rehydrate(request.CatalogKey.Value, row, hreflang))
            .ToArray();

        PublicSitemapCursor? nextCursor = null;
        if (hasMore && selectedRows.Length > 0)
        {
            var last = selectedRows[^1];
            nextCursor = new PublicSitemapCursor(
                activeRevisionId.Value,
                request.CatalogKey,
                request.Locale,
                QuerySeoLocale.Create(last.Locale),
                QuerySeoPath.CreateIndexable(last.Path));
        }

        await transaction.CommitAsync(cancellationToken);
        return new PublicSitemapSlice(
            activeRevisionId.Value,
            documents,
            nextCursor);
    }

    private static async Task<Guid?> ReadActiveRevisionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT pointer.public_read_revision_id
            FROM seo_projection.active_sitemap_revision pointer
            INNER JOIN seo_projection.sitemap_revision revision
                ON revision.catalog_key = pointer.catalog_key
               AND revision.public_read_revision_id = pointer.public_read_revision_id
            WHERE pointer.catalog_key = @catalog_key;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (Guid)value;
    }

    private static async Task<IReadOnlyList<RecordRow>> ReadRecordRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicSitemapPageRequest request,
        Guid activeRevisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT route_kind,
                   locale,
                   path,
                   canonical_path,
                   last_modified_at_utc
            FROM seo_projection.sitemap_record
            WHERE public_read_revision_id = @public_read_revision_id
              AND catalog_key = @catalog_key
              AND (@locale IS NULL OR locale = @locale)
              AND
              (
                  @last_locale IS NULL OR
                  locale > @last_locale OR
                  (locale = @last_locale AND path > @last_path)
              )
            ORDER BY locale, path
            LIMIT @limit;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "public_read_revision_id",
            NpgsqlDbType.Uuid,
            activeRevisionId);
        command.Parameters.AddWithValue(
            "catalog_key",
            NpgsqlDbType.Varchar,
            request.CatalogKey.Value);
        command.Parameters.AddWithValue(
            "locale",
            NpgsqlDbType.Varchar,
            (object?)request.Locale?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "last_locale",
            NpgsqlDbType.Varchar,
            (object?)request.Cursor?.LastLocale.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "last_path",
            NpgsqlDbType.Varchar,
            (object?)request.Cursor?.LastPath.Value ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "limit",
            NpgsqlDbType.Integer,
            checked(request.PageSize + 1));

        var rows = new List<RecordRow>(request.PageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RecordRow(
                reader.GetInt16(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetFieldValue<DateTimeOffset>(4)));
        }

        return rows;
    }

    private static async Task<IReadOnlyDictionary<(string Locale, string Path), IReadOnlyList<QueryHreflangRoute>>>
        ReadHreflangAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            string catalogKey,
            Guid activeRevisionId,
            IReadOnlyList<RecordRow> records,
            CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new Dictionary<(string, string), IReadOnlyList<QueryHreflangRoute>>();
        }

        const string sql = """
            WITH selected(source_locale, source_path) AS
            (
                SELECT *
                FROM unnest(@source_locales::varchar[], @source_paths::varchar[])
            )
            SELECT link.source_locale,
                   link.source_path,
                   link.alternate_locale,
                   link.alternate_path
            FROM seo_projection.sitemap_hreflang link
            INNER JOIN selected
                ON selected.source_locale = link.source_locale
               AND selected.source_path = link.source_path
            WHERE link.public_read_revision_id = @public_read_revision_id
              AND link.catalog_key = @catalog_key
            ORDER BY link.source_locale,
                     link.source_path,
                     link.alternate_locale,
                     link.alternate_path;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "source_locales",
            NpgsqlDbType.Array | NpgsqlDbType.Varchar,
            records.Select(record => record.Locale).ToArray());
        command.Parameters.AddWithValue(
            "source_paths",
            NpgsqlDbType.Array | NpgsqlDbType.Varchar,
            records.Select(record => record.Path).ToArray());
        command.Parameters.AddWithValue(
            "public_read_revision_id",
            NpgsqlDbType.Uuid,
            activeRevisionId);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);

        var mutable = new Dictionary<(string, string), List<QueryHreflangRoute>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = (reader.GetString(0), reader.GetString(1));
            if (!mutable.TryGetValue(key, out var values))
            {
                values = [];
                mutable.Add(key, values);
            }

            values.Add(QueryHreflangRoute.Create(
                reader.GetString(2),
                reader.GetString(3)));
        }

        return mutable.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<QueryHreflangRoute>)pair.Value.ToArray());
    }

    private static QuerySitemapDocument Rehydrate(
        string catalogKey,
        RecordRow row,
        IReadOnlyDictionary<(string Locale, string Path), IReadOnlyList<QueryHreflangRoute>> hreflang)
    {
        try
        {
            if (!hreflang.TryGetValue((row.Locale, row.Path), out var alternateRoutes))
            {
                throw Failure(
                    "QUERY_SITEMAP_PERSISTED_HREFLANG_MISSING",
                    $"Persisted sitemap route '{row.Locale}:{row.Path}' has no hreflang projection.");
            }

            return QuerySitemapDocument.CreateIndexable(
                (QuerySeoRouteKind)row.RouteKind,
                catalogKey,
                row.Locale,
                row.Path,
                row.CanonicalPath,
                alternateRoutes,
                row.LastModifiedAtUtc,
                isDraft: false,
                redirectsToAnotherRoute: false,
                isSuppressed: false);
        }
        catch (QueryDomainException exception)
        {
            throw Failure(
                "QUERY_SITEMAP_PERSISTED_STATE_INVALID",
                "Persisted sitemap state violates Query SEO domain invariants.",
                exception);
        }
    }

    private static QuerySitemapProjectionException Failure(
        string code,
        string detail,
        Exception? innerException = null) =>
        new(
            code,
            detail,
            "Rebuild the exact Query sitemap revision before serving further sitemap pages.",
            innerException);

    private sealed record RecordRow(
        short RouteKind,
        string Locale,
        string Path,
        string CanonicalPath,
        DateTimeOffset LastModifiedAtUtc);
}
