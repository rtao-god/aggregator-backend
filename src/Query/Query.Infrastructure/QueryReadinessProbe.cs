using Npgsql;

namespace Aggregator.Query.Infrastructure;

public enum QueryReadinessState
{
    Ready = 1,
    BlockedNoActiveProjection = 2,
    SchemaUnavailable = 3,
    DatabaseUnavailable = 4,
}

public sealed record QueryReadinessSnapshot(
    QueryReadinessState State,
    int ActiveCatalogCount,
    string Owner,
    string Code,
    string RequiredAction,
    string? Diagnostic);

public sealed class QueryReadinessProbe
{
    private readonly NpgsqlDataSource _dataSource;

    public QueryReadinessProbe(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<QueryReadinessSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            const string schemaSql = """
                SELECT to_regclass('projection.current_public_read') IS NOT NULL,
                       to_regclass('projection.public_read_revision') IS NOT NULL,
                       to_regclass('documents.listing_document') IS NOT NULL;
                """;
            await using (var schemaCommand = new NpgsqlCommand(schemaSql, connection))
            await using (var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken) ||
                    !reader.GetBoolean(0) ||
                    !reader.GetBoolean(1) ||
                    !reader.GetBoolean(2))
                {
                    return new QueryReadinessSnapshot(
                        QueryReadinessState.SchemaUnavailable,
                        0,
                        "Query.Persistence",
                        "QUERY_SCHEMA_UNAVAILABLE",
                        "Run the Query migration owner command against query_db.",
                        "One or more required Query tables are missing.");
                }
            }

            const string pointerSql = "SELECT count(*) FROM projection.current_public_read;";
            await using var pointerCommand = new NpgsqlCommand(pointerSql, connection);
            var result = await pointerCommand.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Query readiness pointer count returned no value.");
            var count = Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
            return count > 0
                ? new QueryReadinessSnapshot(
                    QueryReadinessState.Ready,
                    count,
                    "Query.PublicReadRevision",
                    "QUERY_READY",
                    "No recovery action is required.",
                    null)
                : new QueryReadinessSnapshot(
                    QueryReadinessState.BlockedNoActiveProjection,
                    0,
                    "Query.PublicReadRevision",
                    "QUERY_ACTIVE_PROJECTION_MISSING",
                    "Activate a Catalog publication and complete its Query projection build.",
                    "query_db has no current public-read pointer.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PostgresException exception)
        {
            return new QueryReadinessSnapshot(
                QueryReadinessState.SchemaUnavailable,
                0,
                "Query.Persistence",
                "QUERY_SCHEMA_CHECK_FAILED",
                "Inspect query_db schema and run the Query migration owner command if required.",
                $"PostgreSQL {exception.SqlState}: {exception.MessageText}");
        }
        catch (NpgsqlException exception)
        {
            return new QueryReadinessSnapshot(
                QueryReadinessState.DatabaseUnavailable,
                0,
                "Query.Persistence",
                "QUERY_DATABASE_UNAVAILABLE",
                "Restore query_db connectivity without changing the public read contract.",
                exception.Message);
        }
    }
}
