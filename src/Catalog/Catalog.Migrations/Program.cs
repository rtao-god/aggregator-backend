using Platform.Persistence;

namespace Aggregator.Catalog.Migrations;

public static class Program
{
    public static async Task Main()
    {
        var connectionString = Environment.GetEnvironmentVariable("CATALOG_MIGRATOR_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("CATALOG_MIGRATOR_CONNECTION_STRING is required.");
        }

        var assembly = typeof(Program).Assembly;
        var resourcePrefix = $"{assembly.GetName().Name}.Migrations";
        var scripts = MigrationScript.LoadFromAssembly(assembly, resourcePrefix);
        var runner = new PostgresMigrationRunner(connectionString, "Catalog");
        var applied = await runner.ApplyAsync(scripts, CancellationToken.None);

        Console.WriteLine($"Catalog migrations applied: {applied.Count}.");
    }
}
