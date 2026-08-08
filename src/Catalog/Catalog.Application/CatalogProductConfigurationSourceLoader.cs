using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;

namespace Aggregator.Catalog.Application;

/// <summary>
/// Loads the authored Git product-configuration source into the single Catalog import contract.
/// </summary>
public static class CatalogProductConfigurationSourceLoader
{
    private static readonly string[] RequiredSourceFiles =
    [
        "attributes.json",
        "catalog.json",
        "manifest.json",
        "site.json",
        "taxonomy.json",
    ];
    private static readonly JsonSerializerOptions InputOptions = CreateInputOptions();

    /// <summary>
    /// Reads and strictly validates one complete authored product-configuration directory.
    /// </summary>
    public static async Task<ImportProductConfigurationRequest> LoadAsync(
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var normalizedDirectory = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(normalizedDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Product configuration directory '{normalizedDirectory}' does not exist.");
        }

        ValidateSourceInventory(normalizedDirectory);
        var manifest = await ReadRequiredAsync<ProductConfigurationSourceManifest>(
            normalizedDirectory,
            "manifest.json",
            cancellationToken);
        manifest.Validate();
        var site = await ReadRequiredAsync<SiteDefinitionContract>(
            normalizedDirectory,
            "site.json",
            cancellationToken);
        var catalog = await ReadRequiredAsync<CatalogDefinitionContract>(
            normalizedDirectory,
            "catalog.json",
            cancellationToken);
        var categories = await ReadRequiredAsync<CategoryDefinitionContract[]>(
            normalizedDirectory,
            "taxonomy.json",
            cancellationToken);
        var attributes = await ReadRequiredAsync<AttributeDefinitionContract[]>(
            normalizedDirectory,
            "attributes.json",
            cancellationToken);

        var directoryName = new DirectoryInfo(normalizedDirectory).Name;
        if (!string.Equals(site.Key, directoryName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Product configuration directory '{directoryName}' does not match site key '{site.Key}'.");
        }

        if (!string.Equals(
                manifest.ContractIdentity,
                CatalogContractIdentity.ProductConfiguration,
                StringComparison.Ordinal) ||
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
                attributes),
            manifest.ExpectedContentDigest);
    }

    private static void ValidateSourceInventory(string sourceDirectory)
    {
        var actualEntries = Directory
            .EnumerateFileSystemEntries(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(fileName => fileName is not null)
            .Select(fileName => fileName!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missingFiles = RequiredSourceFiles
            .Except(actualEntries, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var unexpectedEntries = actualEntries
            .Except(RequiredSourceFiles, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (missingFiles.Length == 0 && unexpectedEntries.Length == 0)
        {
            return;
        }

        throw new InvalidDataException(
            "Product configuration source inventory does not match its active contract. " +
            $"Missing: {FormatInventory(missingFiles)}. " +
            $"Unexpected: {FormatInventory(unexpectedEntries)}.");
    }

    private static string FormatInventory(IReadOnlyCollection<string> values) =>
        values.Count == 0 ? "none" : string.Join(", ", values);

    private static async Task<T> ReadRequiredAsync<T>(
        string sourceDirectory,
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(sourceDirectory, fileName);
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
        DateTimeOffset CreatedAtUtc,
        string ExpectedContentDigest)
    {
        public void Validate()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(ContractIdentity);
            if (ContractRevision <= 0)
            {
                throw new InvalidDataException(
                    "Product configuration contract revision must be positive.");
            }

            if (RevisionId == Guid.Empty)
            {
                throw new InvalidDataException(
                    "Product configuration revision ID is required.");
            }

            if (CreatedAtUtc.Offset != TimeSpan.Zero)
            {
                throw new InvalidDataException(
                    "Product configuration creation timestamp must be UTC.");
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(ExpectedContentDigest);
        }
    }
}
