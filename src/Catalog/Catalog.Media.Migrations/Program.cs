using Platform.Persistence;

namespace Aggregator.CatalogMedia.Migrations;

public static class Program
{
    public static async Task Main()
    {
        var connectionString = Environment.GetEnvironmentVariable("CATALOG_MEDIA_MIGRATOR_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("CATALOG_MEDIA_MIGRATOR_CONNECTION_STRING is required.");
        }

        var assembly = typeof(Program).Assembly;
        var resourcePrefix = $"{assembly.GetName().Name}.Migrations";
        var scripts = MigrationScript.LoadFromAssembly(assembly, resourcePrefix);
        var runner = new PostgresMigrationRunner(connectionString, "CatalogMedia");
        var applied = await runner.ApplyAsync(scripts, CancellationToken.None);

        Console.WriteLine($"CatalogMedia migrations applied: {applied.Count}.");
    }
}
