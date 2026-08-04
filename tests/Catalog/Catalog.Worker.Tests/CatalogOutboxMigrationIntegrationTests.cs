using Npgsql;
using NpgsqlTypes;

namespace Catalog.Worker.Tests;

public sealed class CatalogOutboxMigrationIntegrationTests
{
    private const string EnvironmentVariable = "PLATFORM_MESSAGING_TEST_POSTGRES";

    private static readonly string[] RequiredColumns =
    [
        "message_id",
        "routing_key",
        "contract_identity",
        "payload_json",
        "payload_digest",
        "occurred_at_utc",
        "correlation_id",
        "causation_id",
        "lease_token",
        "lease_owner",
        "lease_until_utc",
        "delivery_attempts",
        "dispatched_at_utc",
        "last_error",
        "dead_lettered_at_utc",
        "dead_letter_reason",
    ];

    [Fact]
    public async Task FreshCatalogMigrationsCreateExactWorkerSchema()
    {
        var database = await TemporaryDatabase.TryCreateAsync();
        if (database is null)
        {
            return;
        }

        await using (database)
        {
            await database.ExecuteMigrationAsync("V001__catalog_owner_schema.sql");
            await database.ExecuteMigrationAsync("V002__catalog_durable_outbox.sql");

            var columns = await database.ReadOutboxColumnsAsync();
            Assert.Equal(RequiredColumns.Order(StringComparer.Ordinal), columns.Order(StringComparer.Ordinal));
            Assert.Equal("text", await database.ReadPayloadDataTypeAsync());

            await database.InsertValidOutboxMessageAsync();
            await Assert.ThrowsAsync<PostgresException>(() =>
                database.SetPartialLeaseAsync());
            await Assert.ThrowsAsync<PostgresException>(() =>
                database.SetDeadLetterWithoutReasonAsync());
        }
    }

    [Fact]
    public async Task LegacyRowsBlockUnderdeterminedOutboxUpgrade()
    {
        var database = await TemporaryDatabase.TryCreateAsync();
        if (database is null)
        {
            return;
        }

        await using (database)
        {
            await database.ExecuteMigrationAsync("V001__catalog_owner_schema.sql");
            await database.InsertLegacyOutboxMessageAsync();

            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                database.ExecuteMigrationAsync("V002__catalog_durable_outbox.sql"));

            Assert.Contains(
                "legacy rows lack canonical payload digests and correlation metadata",
                exception.MessageText,
                StringComparison.Ordinal);
        }
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private TemporaryDatabase(
            string adminConnectionString,
            string connectionString,
            string databaseName)
        {
            _adminConnectionString = adminConnectionString;
            ConnectionString = connectionString;
            _databaseName = databaseName;
        }

        public string ConnectionString { get; }

        public static async Task<TemporaryDatabase?> TryCreateAsync()
        {
            var baseConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(baseConnectionString))
            {
                return null;
            }

            var baseBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString);
            var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString)
            {
                Database = "postgres",
                Pooling = false,
            };
            var databaseName = $"catalog_test_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE \"{databaseName}\";";
                await command.ExecuteNonQueryAsync();
            }

            baseBuilder.Database = databaseName;
            baseBuilder.Pooling = false;
            return new TemporaryDatabase(
                adminBuilder.ConnectionString,
                baseBuilder.ConnectionString,
                databaseName);
        }

        public async Task ExecuteMigrationAsync(string fileName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
            var sql = await File.ReadAllTextAsync(Path.Combine(
                FindRepositoryRoot(),
                "src",
                "Catalog",
                "Catalog.Migrations",
                "Migrations",
                fileName));
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task<IReadOnlyList<string>> ReadOutboxColumnsAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT column_name
                FROM information_schema.columns
                WHERE table_schema = 'catalog'
                  AND table_name = 'outbox_message'
                ORDER BY ordinal_position;
                """;
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }

            return columns;
        }

        public async Task<string> ReadPayloadDataTypeAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT data_type
                FROM information_schema.columns
                WHERE table_schema = 'catalog'
                  AND table_name = 'outbox_message'
                  AND column_name = 'payload_json';
                """;
            return (string)(await command.ExecuteScalarAsync()
                ?? throw new InvalidOperationException("Catalog payload column was not found."));
        }

        public async Task InsertValidOutboxMessageAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO catalog.outbox_message
                (
                    message_id,
                    routing_key,
                    contract_identity,
                    payload_json,
                    payload_digest,
                    occurred_at_utc,
                    correlation_id,
                    causation_id
                )
                VALUES
                (
                    @messageId,
                    'catalog.publication.activated',
                    'aggregator.catalog.publication-activated@1',
                    '{}',
                    @payloadDigest,
                    @occurredAtUtc,
                    'corr.catalog-migration:0001',
                    NULL
                );
                """;
            command.Parameters.AddWithValue(
                "messageId",
                NpgsqlDbType.Uuid,
                Guid.Parse("0192f5f0-0000-7000-8000-000000000001"));
            command.Parameters.AddWithValue("payloadDigest", new string('a', 64));
            command.Parameters.AddWithValue(
                "occurredAtUtc",
                NpgsqlDbType.TimestampTz,
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetPartialLeaseAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE catalog.outbox_message
                SET lease_owner = 'catalog-worker-test'
                WHERE message_id = '0192f5f0-0000-7000-8000-000000000001';
                """;
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetDeadLetterWithoutReasonAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE catalog.outbox_message
                SET dead_lettered_at_utc = '2026-01-01T00:00:00Z',
                    dead_letter_reason = NULL
                WHERE message_id = '0192f5f0-0000-7000-8000-000000000001';
                """;
            await command.ExecuteNonQueryAsync();
        }

        public async Task InsertLegacyOutboxMessageAsync()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO catalog.outbox_message
                (
                    id,
                    event_type,
                    event_revision,
                    payload,
                    occurred_at_utc
                )
                VALUES
                (
                    '0192f5f0-0000-7000-8000-000000000001',
                    'catalog.publication.activated',
                    1,
                    '{}',
                    '2026-01-01T00:00:00Z'
                );
                """;
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(_adminConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);";
            await command.ExecuteNonQueryAsync();
        }

        private static string FindRepositoryRoot()
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException("Repository root could not be located.");
        }
    }
}
