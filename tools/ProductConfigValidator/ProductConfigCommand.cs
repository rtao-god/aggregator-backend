using System.Text.Json;
using Aggregator.Catalog.Application;

internal static class ProductConfigCommand
{
    private const string Owner = "Catalog.ProductConfiguration";
    private static readonly JsonSerializerOptions OutputOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length != 2 || !string.Equals(args[0], "validate", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                "Usage: dotnet run --project tools/ProductConfigValidator/ProductConfigValidator.csproj -- validate <product-config-directory>");
            return 64;
        }

        var sourceDirectory = Path.GetFullPath(args[1]);
        try
        {
            var artifact = await CatalogProductConfigurationSourceLoader.LoadAsync(
                sourceDirectory,
                cancellationToken);
            Console.WriteLine(JsonSerializer.Serialize(
                new
                {
                    owner = Owner,
                    artifact.ContractIdentity,
                    artifact.ContractRevision,
                    artifact.Configuration.RevisionId,
                    artifact.Configuration.CreatedAtUtc,
                    siteKey = artifact.Configuration.Site.Key,
                    catalogKey = artifact.Configuration.Catalog.Key,
                    categoryCount = artifact.Configuration.Categories.Count,
                    attributeCount = artifact.Configuration.Attributes.Count,
                    contentDigest = artifact.ExpectedContentDigest,
                },
                OutputOptions));
            return 0;
        }
        catch (Exception exception) when (exception is
                   ArgumentException or
                   InvalidOperationException or
                   JsonException or
                   IOException or
                   UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"owner={Owner} code=PRODUCT_CONFIG_VALIDATION_FAILED path={sourceDirectory} " +
                $"actual={exception.GetType().Name}: {exception.Message} " +
                "requiredAction=Correct the authored product configuration and rerun the exact validator command.");
            return 1;
        }
    }
}
