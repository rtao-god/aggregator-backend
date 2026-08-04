using Platform.Persistence;

namespace Aggregator.Promotion.Overlay.Migrations;

public static class Program
{
    public static async Task Main()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            "PROMOTION_OVERLAY_MIGRATOR_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "PROMOTION_OVERLAY_MIGRATOR_CONNECTION_STRING is required.");
        }

        var assembly = typeof(Program).Assembly;
        var resourcePrefix = $"{assembly.GetName().Name}.Migrations";
        var scripts = MigrationScript.LoadFromAssembly(assembly, resourcePrefix);
        var runner = new PostgresMigrationRunner(connectionString, "PromotionOverlay");
        var applied = await runner.ApplyAsync(scripts, CancellationToken.None);
        Console.WriteLine($"Promotion overlay migrations applied: {applied.Count}.");
    }
}
