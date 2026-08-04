using Npgsql;
using NpgsqlTypes;

namespace Platform.Persistence;

/// <summary>Applies exact owner migrations with advisory locking and checksum-drift rejection.</summary>
public sealed class PostgresMigrationRunner
{
    private readonly string _connectionString;
    private readonly string _owner;

    public PostgresMigrationRunner(string connectionString, string owner)
    {
        _connectionString = string.IsNullOrWhiteSpace(connectionString)
            ? throw new ArgumentException("A PostgreSQL connection string is required.", nameof(connectionString))
            : connectionString;
        _owner = string.IsNullOrWhiteSpace(owner)
            ? throw new ArgumentException("A migration owner is required.", nameof(owner))
            : owner.Trim();
    }

    public async Task<IReadOnlyList<string>> ApplyAsync(
        IReadOnlyList<MigrationScript> scripts,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        if (scripts.Count == 0)
        {
            throw new ArgumentException("At least one migration script is required.", nameof(scripts));
        }

        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await AcquireOwnerLockAsync(connection, cancellationToken);

        try
        {
            await EnsureLedgerAsync(connection, cancellationToken);
            var applied = await ReadAppliedAsync(connection, cancellationToken);
            var completed = new List<string>();

            foreach (var script in scripts.OrderBy(item => item.Version, StringComparer.Ordinal))
            {
                if (applied.TryGetValue(script.Version, out var recordedDigest))
                {
                    if (!string.Equals(recordedDigest, script.Sha256, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Migration '{script.Version}' for owner '{_owner}' has checksum drift. Recorded '{recordedDigest}', current '{script.Sha256}'.");
                    }

                    continue;
                }

                await ApplyOneAsync(connection, script, cancellationToken);
                completed.Add(script.Version);
            }

            return completed;
        }
        finally
        {
            await ReleaseOwnerLockAsync(connection, CancellationToken.None);
        }
    }

    private async Task AcquireOwnerLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_lock(hashtextextended(@owner, 0));", connection);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Text, _owner);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReleaseOwnerLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("select pg_advisory_unlock(hashtextextended(@owner, 0));", connection);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Text, _owner);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureLedgerAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            create schema if not exists platform;
            create table if not exists platform.schema_migration (
                owner text not null,
                version text not null,
                sha256 text not null,
                applied_at_utc timestamptz not null,
                primary key (owner, version),
                constraint ck_schema_migration_sha256 check (sha256 ~ '^[0-9a-f]{64}$')
            );
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<Dictionary<string, string>> ReadAppliedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = "select version, sha256 from platform.schema_migration where owner = @owner order by version;";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("owner", NpgsqlDbType.Text, _owner);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0), reader.GetString(1));
        }

        return result;
    }

    private async Task ApplyOneAsync(
        NpgsqlConnection connection,
        MigrationScript script,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var migration = new NpgsqlCommand(script.Sql, connection, transaction))
            {
                migration.CommandTimeout = 300;
                await migration.ExecuteNonQueryAsync(cancellationToken);
            }

            const string ledgerSql = """
                insert into platform.schema_migration (owner, version, sha256, applied_at_utc)
                values (@owner, @version, @sha256, @appliedAtUtc);
                """;
            await using (var ledger = new NpgsqlCommand(ledgerSql, connection, transaction))
            {
                ledger.Parameters.AddWithValue("owner", NpgsqlDbType.Text, _owner);
                ledger.Parameters.AddWithValue("version", NpgsqlDbType.Text, script.Version);
                ledger.Parameters.AddWithValue("sha256", NpgsqlDbType.Text, script.Sha256);
                ledger.Parameters.AddWithValue("appliedAtUtc", NpgsqlDbType.TimestampTz, DateTimeOffset.UtcNow);
                await ledger.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
