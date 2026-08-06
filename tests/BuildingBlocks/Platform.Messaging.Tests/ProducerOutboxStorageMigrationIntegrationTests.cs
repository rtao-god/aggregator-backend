using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace Platform.Messaging.Tests;

public sealed class ProducerOutboxStorageMigrationIntegrationTests
{
    [Theory]
    [InlineData(
        "src/Catalog/Catalog.Migrations/Migrations",
        "catalog")]
    [InlineData(
        "src/Catalog/Catalog.Media.Migrations/Migrations",
        "media_messaging")]
    [InlineData(
        "src/Promotion/Promotion.Migrations/Migrations",
        "messaging")]
    public async Task FreshProducerMigrationsPreserveExactOutboxPayloadText(
        string migrationDirectory,
        string schema)
    {
        await using var database = await TemporaryDatabase.CreateAsync();

        await database.ApplyAllAsync(migrationDirectory);

        Assert.Equal(
            "text",
            await database.ScalarAsync<string>(
                """
                SELECT data_type
                FROM information_schema.columns
                WHERE table_schema = @schema
                  AND table_name = 'outbox_message'
                  AND column_name = 'payload_json';
                """,
                new NpgsqlParameter<string>("schema", schema)));

        const string payload = "{ \"second\": 2, \"first\": 1 }";
        var digest = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
        var messageId = Guid.CreateVersion7();
        await database.ExecuteAsync(
            $"""
            INSERT INTO {schema}.outbox_message
            (
                message_id,
                routing_key,
                contract_identity,
                payload_json,
                payload_digest,
                occurred_at_utc,
                correlation_id
            )
            VALUES
            (
                @message_id,
                'owner.event.changed',
                'aggregator.owner.event-changed@1',
                @payload_json,
                @payload_digest,
                '2026-08-06T10:00:00Z',
                'corr.outbox-storage:0001'
            );
            """,
            new NpgsqlParameter<Guid>("message_id", messageId),
            new NpgsqlParameter<string>("payload_json", payload),
            new NpgsqlParameter<string>("payload_digest", digest));

        Assert.Equal(
            payload,
            await database.ScalarAsync<string>(
                $"SELECT payload_json FROM {schema}.outbox_message WHERE message_id = @message_id;",
                new NpgsqlParameter<Guid>("message_id", messageId)));
    }

    private sealed class TemporaryDatabase : IAsyncDisposable
    {
        private const string EnvironmentVariable = "PLATFORM_MESSAGING_TEST_POSTGRES";
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

        private string ConnectionString { get; }

        public static async Task<TemporaryDatabase> CreateAsync()
        {
            var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                throw new InvalidOperationException(
                    $"Environment variable '{EnvironmentVariable}' is required for producer migration integration proof.");
            }

            var adminBuilder = new NpgsqlConnectionStringBuilder(configured)
            {
                Database = "postgres",
                Pooling = false,
            };
            var databaseName = $"outbox_storage_{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE \"{databaseName}\";";
                await command.ExecuteNonQueryAsync();
            }

            var databaseBuilder = new NpgsqlConnectionStringBuilder(configured)
            {
                Database = databaseName,
                Pooling = false,
            };
            return new TemporaryDatabase(
                adminBuilder.ConnectionString,
                databaseBuilder.ConnectionString,
                databaseName);
        }

        public async Task ApplyAllAsync(string relativeDirectory)
        {
            var directory = Path.Combine(
                FindRepositoryRoot(),
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            foreach (var path in Directory
                         .EnumerateFiles(directory, "*.sql", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = await File.ReadAllTextAsync(path);
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task ExecuteAsync(
            string sql,
            params NpgsqlParameter[] parameters)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(parameters);
            await command.ExecuteNonQueryAsync();
        }

        public async Task<T> ScalarAsync<T>(
            string sql,
            params NpgsqlParameter[] parameters)
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddRange(parameters);
            var value = await command.ExecuteScalarAsync();
            return value is not null and not DBNull
                ? (T)Convert.ChangeType(
                    value,
                    typeof(T),
                    System.Globalization.CultureInfo.InvariantCulture)
                : throw new InvalidOperationException(
                    "Producer outbox payload column was not found after migrations.");
        }

        public async ValueTask DisposeAsync()
        {
            NpgsqlConnection.ClearAllPools();
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
