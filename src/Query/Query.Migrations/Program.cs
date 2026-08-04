using Platform.Persistence;

namespace Aggregator.Query.Migrations;

public static class Program
{
    public static async Task Main()
    {
        var connectionString = Environment.GetEnvironmentVariable("QUERY_MIGRATOR_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("QUERY_MIGRATOR_CONNECTION_STRING is required.");
        }

        var assembly = typeof(Program).Assembly;
        var resourcePrefix = $"{assembly.GetName().Name}.Migrations";
        var scripts = MigrationScript.LoadFromAssembly(assembly, resourcePrefix);
        var runner = new PostgresMigrationRunner(connectionString, "Query");
        var applied = await runner.ApplyAsync(scripts, CancellationToken.None);

        Console.WriteLine($"Query migrations applied: {applied.Count}.");
    }
}
