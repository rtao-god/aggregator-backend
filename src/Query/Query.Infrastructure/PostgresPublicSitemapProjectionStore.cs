using System.Data;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

/// <summary>Query-owned immutable sitemap revision and optimistic active-pointer store.</summary>
public sealed class PostgresPublicSitemapProjectionStore(NpgsqlDataSource dataSource)
    : IPublicSitemapProjectionStore
{
    public async Task<PublicSitemapProjectionResult> ActivateAsync(
        PublicSitemapProjectionArtifact artifact,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        try
        {
            await LockCatalogAsync(
                connection,
                transaction,
                artifact.CatalogKey.Value,
                cancellationToken);
            var currentRevisionId = await ReadCurrentRevisionIdAsync(
                connection,
                transaction,
                artifact.CatalogKey.Value,
                cancellationToken);
            var existingRevision = await ReadRevisionAsync(
                connection,
                transaction,
                artifact.CatalogKey.Value,
                artifact.PublicReadRevisionId,
                cancellationToken);
            if (existingRevision is not null)
            {
                EnsureExactRevisionReplay(artifact, existingRevision);
                if (currentRevisionId == artifact.PublicReadRevisionId)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return new PublicSitemapProjectionResult(
                        artifact.PublicReadRevisionId,
                        PublicSitemapProjectionDisposition.Duplicate);
                }
            }

            EnsureExpectedPointer(artifact, currentRevisionId);
            if (existingRevision is null)
            {
                await InsertRevisionAsync(
                    connection,
                    transaction,
                    artifact,
                    cancellationToken);
                await InsertRecordsAsync(
                    connection,
                    transaction,
                    artifact,
                    cancellationToken);
                await InsertHreflangAsync(
                    connection,
                    transaction,
                    artifact,
                    cancellationToken);
            }

            await ActivatePointerAsync(
                connection,
                transaction,
                artifact,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PublicSitemapProjectionResult(
                artifact.PublicReadRevisionId,
                PublicSitemapProjectionDisposition.Applied);
        }
        catch (PostgresException exception) when (IsSitemapContractFailure(exception))
        {
            throw Failure(
                "QUERY_SITEMAP_PERSISTENCE_CONTRACT_FAILED",
                $"PostgreSQL rejected the sitemap revision contract ({exception.SqlState}).",
                exception);
        }
    }

    private static async Task LockCatalogAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));",
            connection,
            transaction);
        command.Parameters.AddWithValue(
            "key",
            NpgsqlDbType.Text,
            $"query-sitemap:{catalogKey}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<Guid?> ReadCurrentRevisionIdAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT public_read_revision_id
            FROM seo_projection.active_sitemap_revision
            WHERE catalog_key = @catalog_key
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (Guid)value;
    }

    private static async Task<RevisionRow?> ReadRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string catalogKey,
        Guid publicReadRevisionId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT content_digest, record_count, built_at_utc
            FROM seo_projection.sitemap_revision
            WHERE catalog_key = @catalog_key
              AND public_read_revision_id = @public_read_revision_id
            FOR UPDATE;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("catalog_key", NpgsqlDbType.Varchar, catalogKey);
        command.Parameters.AddWithValue(
            "public_read_revision_id",
            NpgsqlDbType.Uuid,
            publicReadRevisionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RevisionRow(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetFieldValue<DateTimeOffset>(2));
    }

    private static void EnsureExactRevisionReplay(
        PublicSitemapProjectionArtifact artifact,
        RevisionRow existing)
    {
        if (!string.Equals(
                existing.ContentDigest,
                artifact.ContentDigest,
                StringComparison.Ordinal) ||
            existing.RecordCount != artifact.Records.Count ||
            existing.BuiltAtUtc != artifact.BuiltAtUtc)
        {
            throw Failure(
                "QUERY_SITEMAP_REVISION_IDENTITY_CONFLICT",
                "The public-read revision identity already owns a different sitemap artifact.");
        }
    }

    private static void EnsureExpectedPointer(
        PublicSitemapProjectionArtifact artifact,
        Guid? currentRevisionId)
    {
        if (artifact.ExpectedCurrentPublicReadRevisionId != currentRevisionId)
        {
            throw Failure(
                "QUERY_SITEMAP_POINTER_CONFLICT",
                $"Expected active sitemap revision '{Format(artifact.ExpectedCurrentPublicReadRevisionId)}' " +
                $"but found '{Format(currentRevisionId)}'.");
        }
    }

    private static async Task InsertRevisionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicSitemapProjectionArtifact artifact,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO seo_projection.sitemap_revision
            (
                catalog_key,
                public_read_revision_id,
                content_digest,
                record_count,
                built_at_utc
            )
            VALUES
            (
                @catalog_key,
                @public_read_revision_id,
                @content_digest,
                @record_count,
                @built_at_utc
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "catalog_key",
            NpgsqlDbType.Varchar,
            artifact.CatalogKey.Value);
        command.Parameters.AddWithValue(
            "public_read_revision_id",
            NpgsqlDbType.Uuid,
            artifact.PublicReadRevisionId);
        command.Parameters.AddWithValue(
            "content_digest",
            NpgsqlDbType.Char,
            artifact.ContentDigest);
        command.Parameters.AddWithValue(
            "record_count",
            NpgsqlDbType.Integer,
            artifact.Records.Count);
        command.Parameters.AddWithValue(
            "built_at_utc",
            NpgsqlDbType.TimestampTz,
            artifact.BuiltAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertRecordsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicSitemapProjectionArtifact artifact,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO seo_projection.sitemap_record
            (
                public_read_revision_id,
                route_kind,
                catalog_key,
                locale,
                path,
                canonical_path,
                last_modified_at_utc
            )
            VALUES
            (
                @public_read_revision_id,
                @route_kind,
                @catalog_key,
                @locale,
                @path,
                @canonical_path,
                @last_modified_at_utc
            );
            """;
        foreach (var record in artifact.Records)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue(
                "public_read_revision_id",
                NpgsqlDbType.Uuid,
                artifact.PublicReadRevisionId);
            command.Parameters.AddWithValue(
                "route_kind",
                NpgsqlDbType.Smallint,
                (short)record.RouteKind);
            command.Parameters.AddWithValue(
                "catalog_key",
                NpgsqlDbType.Varchar,
                artifact.CatalogKey.Value);
            command.Parameters.AddWithValue(
                "locale",
                NpgsqlDbType.Varchar,
                record.Locale.Value);
            command.Parameters.AddWithValue(
                "path",
                NpgsqlDbType.Varchar,
                record.Path.Value);
            command.Parameters.AddWithValue(
                "canonical_path",
                NpgsqlDbType.Varchar,
                record.CanonicalPath.Value);
            command.Parameters.AddWithValue(
                "last_modified_at_utc",
                NpgsqlDbType.TimestampTz,
                record.LastModifiedAtUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task InsertHreflangAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicSitemapProjectionArtifact artifact,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO seo_projection.sitemap_hreflang
            (
                public_read_revision_id,
                catalog_key,
                source_locale,
                source_path,
                alternate_locale,
                alternate_path
            )
            VALUES
            (
                @public_read_revision_id,
                @catalog_key,
                @source_locale,
                @source_path,
                @alternate_locale,
                @alternate_path
            );
            """;
        foreach (var record in artifact.Records)
        {
            foreach (var alternate in record.Hreflang)
            {
                await using var command = new NpgsqlCommand(sql, connection, transaction);
                command.Parameters.AddWithValue(
                    "public_read_revision_id",
                    NpgsqlDbType.Uuid,
                    artifact.PublicReadRevisionId);
                command.Parameters.AddWithValue(
                    "catalog_key",
                    NpgsqlDbType.Varchar,
                    artifact.CatalogKey.Value);
                command.Parameters.AddWithValue(
                    "source_locale",
                    NpgsqlDbType.Varchar,
                    record.Locale.Value);
                command.Parameters.AddWithValue(
                    "source_path",
                    NpgsqlDbType.Varchar,
                    record.Path.Value);
                command.Parameters.AddWithValue(
                    "alternate_locale",
                    NpgsqlDbType.Varchar,
                    alternate.Locale.Value);
                command.Parameters.AddWithValue(
                    "alternate_path",
                    NpgsqlDbType.Varchar,
                    alternate.Path.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task ActivatePointerAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PublicSitemapProjectionArtifact artifact,
        CancellationToken cancellationToken)
    {
        const string sql = """
            INSERT INTO seo_projection.active_sitemap_revision
            (
                catalog_key,
                public_read_revision_id,
                activated_at_utc
            )
            VALUES
            (
                @catalog_key,
                @public_read_revision_id,
                @activated_at_utc
            )
            ON CONFLICT (catalog_key)
            DO UPDATE SET
                public_read_revision_id = EXCLUDED.public_read_revision_id,
                activated_at_utc = EXCLUDED.activated_at_utc;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(
            "catalog_key",
            NpgsqlDbType.Varchar,
            artifact.CatalogKey.Value);
        command.Parameters.AddWithValue(
            "public_read_revision_id",
            NpgsqlDbType.Uuid,
            artifact.PublicReadRevisionId);
        command.Parameters.AddWithValue(
            "activated_at_utc",
            NpgsqlDbType.TimestampTz,
            artifact.BuiltAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsSitemapContractFailure(PostgresException exception) =>
        exception.SqlState is
            "23503" or
            "23505" or
            "23514" or
            "P7606" or
            "P7607" or
            "P7608" or
            "P7609" or
            "P7610" or
            "P7611" or
            "P7612";

    private static string Format(Guid? value) =>
        value?.ToString("D") ?? "<none>";

    private static QuerySitemapProjectionException Failure(
        string code,
        string detail,
        Exception? innerException = null) =>
        new(
            code,
            detail,
            "Replay or rebuild the exact sitemap revision after restoring Query projection consistency.",
            innerException);

    private sealed record RevisionRow(
        string ContentDigest,
        int RecordCount,
        DateTimeOffset BuiltAtUtc);
}
