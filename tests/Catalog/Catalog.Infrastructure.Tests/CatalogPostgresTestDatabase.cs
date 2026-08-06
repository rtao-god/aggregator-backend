using Aggregator.Catalog.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Catalog.Infrastructure.Tests;

internal sealed class CatalogPostgresTestDatabase : IAsyncDisposable
{
    private const string EnvironmentVariable = "PLATFORM_MESSAGING_TEST_POSTGRES";
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private CatalogPostgresTestDatabase(
        string adminConnectionString,
        string connectionString,
        string databaseName)
    {
        _adminConnectionString = adminConnectionString;
        ConnectionString = connectionString;
        _databaseName = databaseName;
    }

    public string ConnectionString { get; }

    public static async Task<CatalogPostgresTestDatabase> CreateAsync()
    {
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                $"Environment variable '{EnvironmentVariable}' is required for Catalog PostgreSQL integration proof.");
        }

        var baseBuilder = new NpgsqlConnectionStringBuilder(configured)
        {
            Pooling = false,
        };
        var adminBuilder = new NpgsqlConnectionStringBuilder(configured)
        {
            Database = "postgres",
            Pooling = false,
        };
        var databaseName = $"catalog_integration_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\";";
            await command.ExecuteNonQueryAsync();
        }

        baseBuilder.Database = databaseName;
        return new CatalogPostgresTestDatabase(
            adminBuilder.ConnectionString,
            baseBuilder.ConnectionString,
            databaseName);
    }

    public CatalogDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new CatalogDbContext(options);
    }

    public async Task ApplyAllCatalogMigrationsAsync()
    {
        var migrationDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Catalog",
            "Catalog.Migrations",
            "Migrations");
        foreach (var path in Directory
                     .EnumerateFiles(migrationDirectory, "*.sql", SearchOption.TopDirectoryOnly)
                     .OrderBy(Path.GetFileName, StringComparer.Ordinal))
        {
            await ExecuteSqlFileAsync(path);
        }
    }

    public Task ExecuteCatalogMigrationAsync(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return ExecuteSqlFileAsync(Path.Combine(
            FindRepositoryRoot(),
            "src",
            "Catalog",
            "Catalog.Migrations",
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
            throw new InvalidOperationException("Catalog integration query returned no scalar value.");
        }

        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        string schema,
        string table)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema
              AND table_name = @table
            ORDER BY ordinal_position;
            """;
        command.Parameters.AddWithValue("schema", NpgsqlDbType.Text, schema);
        command.Parameters.AddWithValue("table", NpgsqlDbType.Text, table);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(reader.GetString(0));
        }

        return result;
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

    private async Task ExecuteSqlFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Catalog migration was not found.", path);
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
