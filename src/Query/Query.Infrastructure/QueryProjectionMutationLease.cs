using Aggregator.Query.Application;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

internal sealed class QueryProjectionMutationLease : IAsyncDisposable
{
    private const long LockSeed = 72623859790382856;
    private readonly string _catalogKey;
    private NpgsqlConnection? _connection;

    private QueryProjectionMutationLease(
        NpgsqlConnection connection,
        string catalogKey)
    {
        _connection = connection;
        _catalogKey = catalogKey;
    }

    public NpgsqlConnection Connection => _connection
        ?? throw new ObjectDisposedException(nameof(QueryProjectionMutationLease));

    public static async Task<QueryProjectionMutationLease> AcquireAsync(
        NpgsqlDataSource dataSource,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_lock(hashtextextended(@catalog_key, @lock_seed));",
                connection);
            command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
            command.Parameters.Add(new NpgsqlParameter<long>("lock_seed", LockSeed));
            _ = await command.ExecuteScalarAsync(cancellationToken);
            return new QueryProjectionMutationLease(connection, catalogKey);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is null)
        {
            return;
        }

        try
        {
            await using var command = new NpgsqlCommand(
                "SELECT pg_advisory_unlock(hashtextextended(@catalog_key, @lock_seed));",
                connection);
            command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", _catalogKey));
            command.Parameters.Add(new NpgsqlParameter<long>("lock_seed", LockSeed));
            _ = await command.ExecuteScalarAsync(CancellationToken.None);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    public async Task EnsureNoPublicationRecompositionAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_event_id
            FROM projection.publication_overlay_recomposition
            WHERE catalog_key = @catalog_key;
            """;
        await using var command = new NpgsqlCommand(sql, Connection);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", _catalogKey));
        var eventId = await command.ExecuteScalarAsync(cancellationToken);
        if (eventId is Guid blockedEventId)
        {
            throw new QueryProjectionException(
                "Query.PublicationRecomposition",
                "QUERY_PUBLICATION_RECOMPOSITION_PENDING",
                503,
                $"Catalog '{_catalogKey}' is recomposing overlays for publication event '{blockedEventId}'.",
                "Retry the overlay event after the Catalog publication recomposition completes.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = _catalogKey,
                    ["publicationEventId"] = blockedEventId,
                });
        }
    }
}
