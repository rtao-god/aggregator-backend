using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;

internal static class ProductConfigCommand
{
    private const string Owner = "Catalog.ProductConfiguration";
    private static readonly JsonSerializerOptions InputOptions = CreateInputOptions();
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
            var artifact = await LoadAndValidateAsync(sourceDirectory, cancellationToken);
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

    private static async Task<ImportProductConfigurationRequest> LoadAndValidateAsync(
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Product configuration directory '{sourceDirectory}' does not exist.");
        }

        var manifest = await ReadRequiredAsync<ProductConfigurationSourceManifest>(
            sourceDirectory,
            "manifest.json",
            cancellationToken);
        manifest.Validate();
        var site = await ReadRequiredAsync<SiteDefinitionContract>(
            sourceDirectory,
            "site.json",
            cancellationToken);
        var catalog = await ReadRequiredAsync<CatalogDefinitionContract>(
            sourceDirectory,
            "catalog.json",
            cancellationToken);
        var categories = await ReadRequiredAsync<CategoryDefinitionContract[]>(
            sourceDirectory,
            "taxonomy.json",
            cancellationToken);
        var attributes = await ReadRequiredAsync<AttributeDefinitionContract[]>(
            sourceDirectory,
            "attributes.json",
            cancellationToken);

        var directoryName = new DirectoryInfo(sourceDirectory).Name;
        if (!string.Equals(site.Key, directoryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Product configuration directory '{directoryName}' does not match site key '{site.Key}'.");
        }

        if (!string.Equals(manifest.ContractIdentity, CatalogContractIdentity.ProductConfiguration, StringComparison.Ordinal) ||
            manifest.ContractRevision != CatalogContractIdentity.ProductConfigurationRevision)
        {
            throw new InvalidDataException(
                $"Manifest contract '{manifest.ContractIdentity}@{manifest.ContractRevision}' is not supported by Catalog.");
        }

        return CatalogProductConfigurationArtifactBuilder.BuildImportRequest(
            new ProductConfigurationContract(
                manifest.RevisionId,
                manifest.CreatedAtUtc,
                site,
                catalog,
                categories,
                attributes));
    }

    private static async Task<T> ReadRequiredAsync<T>(
        string sourceDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(sourceDirectory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Required product configuration file '{fileName}' does not exist.",
                path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(
                stream,
                InputOptions,
                cancellationToken)
            ?? throw new InvalidDataException(
                $"Product configuration file '{fileName}' contains a null document.");
    }

    private static JsonSerializerOptions CreateInputOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            AllowTrailingCommas = false,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(
            new JsonStringEnumConverter(
                JsonNamingPolicy.CamelCase,
                allowIntegerValues: false));
        return options;
    }

    private sealed record ProductConfigurationSourceManifest(
        string ContractIdentity,
        int ContractRevision,
        Guid RevisionId,
        DateTimeOffset CreatedAtUtc)
    {
        public void Validate()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ContractIdentity);
            if (ContractRevision <= 0)
            {
                throw new InvalidDataException("Product configuration contract revision must be positive.");
            }

            if (RevisionId == Guid.Empty)
            {
                throw new InvalidDataException("Product configuration revision ID is required.");
            }

            if (CreatedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException("Product configuration creation timestamp must be UTC.");
            }
        }
    }
}
