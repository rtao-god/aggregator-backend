using Npgsql;
using NpgsqlTypes;

namespace Query.Infrastructure.Tests;

internal sealed class QueryPostgresTestDatabase : IAsyncDisposable
{
    private const string EnvironmentVariable = "PLATFORM_MESSAGING_TEST_POSTGRES";
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private QueryPostgresTestDatabase(
        string adminConnectionString,
        string connectionString,
        string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        ConnectionString = connectionString;
        _databaseName = databaseName;
    }

    public string ConnectionString { get; }

    public static async Task<QueryPostgresTestDatabase> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"Environment variable '{EnvironmentVariable}' is required for Query PostgreSQL integration proof.");
        }

        var baseBuilder = new NpgsqlConnectionStringBuilder(configured)
        {
            IncludeErrorDetail = true,
            Pooling = false,
        };
        var adminBuilder = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            IncludeErrorDetail = true,
            Pooling = false,
        };
        var databaseName = $"query_integration_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\";";
            await command.ExecuteNonQueryAsync();
        }

        baseBuilder.Database = databaseName;
        return new QueryPostgresTestDatabase(
            adminBuilder.ConnectionString,
            baseBuilder.ConnectionString,
            databaseName);
    }

    public async Task ApplyAllQueryMigrationsAsync()
    {
        var migrationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Query",
            "Query.Migrations",
            "Migrations");
        foreach (var path in Directory
                     .EnumerateFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            await ExecuteSqlFileAsync(path);
        }
    }

    public Task ExecuteQueryMigrationAsync(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return ExecuteSqlFileAsync(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Query",
            "Query.Migrations",
            "Migrations",
            fileName));
    }

    public async Task ExecuteAsync(
        string sql,
        params NpgsqlParameter[] parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
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
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("Query integration query returned no scalar value.");
        }

        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
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

    public static NpgsqlParameter UtcParameter(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz)
        {
            Value = value,
        };

    private async Task ExecuteSqlFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Query migration was not found.", path);
        }

        var sql = await File.ReadAllTextAsync(path);
        await ExecuteAsync(sql);
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
