using System.Text.Json;
using System.Text.Json.Nodes;
using Aggregator.Catalog.Application;

namespace Catalog.Application.Tests;

public sealed class CatalogProductConfigurationSourceLoaderTests
{
    [Fact]
    public async Task UnexpectedSourceEntryFailsClosed()
    {
        await WithBerlinSourceCopyAsync(async sourceDirectory =>
        {
            await File.WriteAllTextAsync(
                Path.Combine(sourceDirectory, "ignored.json"),
                "{}",
                CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CatalogProductConfigurationSourceLoader.LoadAsync(
                    sourceDirectory,
                    CancellationToken.None));

            Assert.Contains("Unexpected: ignored.json", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Missing: none", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task MissingSourceFileFailsClosed()
    {
        await WithBerlinSourceCopyAsync(async sourceDirectory =>
        {
            File.Delete(Path.Combine(sourceDirectory, "taxonomy.json"));

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
                CatalogProductConfigurationSourceLoader.LoadAsync(
                    sourceDirectory,
                    CancellationToken.None));

            Assert.Contains("Missing: taxonomy.json", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Unexpected: none", exception.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task UnknownJsonMemberFailsClosed()
    {
        await WithBerlinSourceCopyAsync(async sourceDirectory =>
        {
            var sitePath = Path.Combine(sourceDirectory, "site.json");
            var site = JsonNode.Parse(await File.ReadAllTextAsync(sitePath))?.AsObject()
                ?? throw new InvalidOperationException("Berlin site fixture is not a JSON object.");
            site["unknownOwner"] = true;
            await File.WriteAllTextAsync(sitePath, site.ToJsonString());

            var exception = await Assert.ThrowsAsync<JsonException>(() =>
                CatalogProductConfigurationSourceLoader.LoadAsync(
                    sourceDirectory,
                    CancellationToken.None));

            Assert.Contains("unknownOwner", exception.Message, StringComparison.Ordinal);
        });
    }

    private static async Task WithBerlinSourceCopyAsync(Func<string, Task> assertion)
    {
        ArgumentNullException.ThrowIfNull(assertion);
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            $"catalog-product-config-{Guid.NewGuid():N}");
        var sourceDirectory = Path.Combine(temporaryRoot, "berlin-recording");
        Directory.CreateDirectory(sourceDirectory);
        try
        {
            var repositorySource = Path.Combine(
                FindRepositoryRoot(),
                "product-config",
                "berlin-recording");
            foreach (var sourcePath in Directory.EnumerateFiles(
                         repositorySource,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                File.Copy(
                    sourcePath,
                    Path.Combine(sourceDirectory, Path.GetFileName(sourcePath)));
            }

            await assertion(sourceDirectory);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
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
