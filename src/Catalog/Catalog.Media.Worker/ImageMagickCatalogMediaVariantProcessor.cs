using System.Diagnostics;
using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Domain;

namespace Aggregator.Catalog.Media.Worker;

public sealed class ImageMagickCatalogMediaVariantProcessor : ICatalogMediaVariantProcessor
{
    public async Task<IReadOnlyList<CatalogMediaVariantContent>> CreateVariantsAsync(
        string sourceContentType,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContentType);
        ArgumentNullException.ThrowIfNull(source);
        var extension = sourceContentType switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            _ => throw Failure(
                "CATALOG_MEDIA_SOURCE_TYPE_UNSUPPORTED",
                $"Image source type '{sourceContentType}' is unsupported."),
        };
        var directory = Path.Combine(
            Path.GetTempPath(),
            "aggregator",
            "catalog-media",
            Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var input = Path.Combine(directory, $"source.{extension}");
            await using (var output = File.Create(input))
            {
                await source.CopyToAsync(output, cancellationToken);
            }
            var originalDimensions = await IdentifyAsync(input, cancellationToken);
            var result = new List<CatalogMediaVariantContent>
            {
                new(
                    CatalogMediaVariantKind.Original,
                    sourceContentType,
                    await File.ReadAllBytesAsync(input, cancellationToken),
                    originalDimensions.Width,
                    originalDimensions.Height),
            };
            result.Add(await CreateWebpAsync(input, directory, CatalogMediaVariantKind.Thumbnail, "320x320>", cancellationToken));
            result.Add(await CreateWebpAsync(input, directory, CatalogMediaVariantKind.Card, "800x600>", cancellationToken));
            result.Add(await CreateWebpAsync(input, directory, CatalogMediaVariantKind.Gallery, "1600x1200>", cancellationToken));
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static async Task<CatalogMediaVariantContent> CreateWebpAsync(
        string input,
        string directory,
        CatalogMediaVariantKind kind,
        string geometry,
        CancellationToken cancellationToken)
    {
        var output = Path.Combine(directory, $"{kind.ToString().ToLowerInvariant()}.webp");
        await RunAsync(
            [input, "-auto-orient", "-strip", "-thumbnail", geometry, "-quality", "82", output],
            cancellationToken);
        var dimensions = await IdentifyAsync(output, cancellationToken);
        return new CatalogMediaVariantContent(
            kind,
            "image/webp",
            await File.ReadAllBytesAsync(output, cancellationToken),
            dimensions.Width,
            dimensions.Height);
    }

    private static async Task<(int Width, int Height)> IdentifyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var output = await RunAsync(["identify", "-format", "%w,%h", path], cancellationToken);
        var parts = output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var width) || !int.TryParse(parts[1], out var height))
            throw Failure("CATALOG_MEDIA_IDENTIFY_INVALID", "ImageMagick returned invalid dimensions.");
        return (width, height);
    }

    private static async Task<string> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("magick")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw Failure("CATALOG_MEDIA_IMAGEMAGICK_START_FAILED", "ImageMagick process could not start.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await standardOutput;
        var error = await standardError;
        if (process.ExitCode != 0)
            throw Failure(
                "CATALOG_MEDIA_IMAGEMAGICK_FAILED",
                $"ImageMagick failed with exit code '{process.ExitCode}': {error.Trim()}"[..Math.Min(
                    $"ImageMagick failed with exit code '{process.ExitCode}': {error.Trim()}".Length,
                    2000)]);
        return output;
    }

    private static CatalogMediaApplicationException Failure(string code, string message) =>
        new("Catalog.Media.Variants", code, 422, message,
            "Correct the source image or restore the ImageMagick runtime before retrying.");
}
