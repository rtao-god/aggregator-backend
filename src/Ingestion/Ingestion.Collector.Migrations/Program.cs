using Platform.Persistence;

namespace Aggregator.Ingestion.Collector.Migrations;

public static class Program
{
    public static async Task Main()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "INGESTION_COLLECTOR_MIGRATOR_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "INGESTION_COLLECTOR_MIGRATOR_CONNECTION_STRING is required.");
        }

        var assembly = typeof(Program).Assembly;
        var resourcePrefix = $"{assembly.GetName().Name}.Migrations";
        var scripts = MigrationScript.LoadFromAssembly(assembly, resourcePrefix);
        var runner = new PostgresMigrationRunner(connectionString, "IngestionCollector");
        var applied = await runner.ApplyAsync(scripts, CancellationToken.None);
        Console.WriteLine($"Ingestion collector migrations applied: {applied.Count}.");
    }
}
