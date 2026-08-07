using Aggregator.Query.Application;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

public sealed class NpgsqlQueryActivationCheckpointReader(NpgsqlDataSource dataSource)
    : IQueryActivationCheckpointReader
{
    public async Task<long?> GetLastActivationRevisionAsync(
        string catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        await using var command = dataSource.CreateCommand(
            """
            SELECT last_activation_revision
            FROM projection.catalog_activation_checkpoint
            WHERE catalog_key = @catalog_key;
            """);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull
            ? null
            : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
