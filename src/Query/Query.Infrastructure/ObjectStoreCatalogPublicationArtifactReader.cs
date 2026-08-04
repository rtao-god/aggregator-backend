using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Contracts;
using Aggregator.Query.Application;
using Platform.ObjectStorage;

namespace Aggregator.Query.Infrastructure;

public sealed class ObjectStoreCatalogPublicationArtifactReader : ICatalogPublicationArtifactReader
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IObjectStore _objectStore;
    private readonly QueryPublicationArtifactReaderOptions _options;

    public ObjectStoreCatalogPublicationArtifactReader(
        IObjectStore objectStore,
        QueryPublicationArtifactReaderOptions options)
    {
        _objectStore = objectStore ?? throw new ArgumentNullException(nameof(objectStore));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<CatalogPublicationArtifact> ReadAsync(
        string objectKey,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        ValidateObjectKey(objectKey);
        ValidateDigest(expectedDigest);

        await using var source = await _objectStore.OpenReadVerifiedAsync(
            objectKey,
            expectedDigest,
            cancellationToken);
        await using var boundedCopy = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (boundedCopy.Length + read > _options.MaximumArtifactBytes)
            {
                throw Failure(
                    "QUERY_PUBLICATION_ARTIFACT_TOO_LARGE",
                    $"Catalog publication artifact '{objectKey}' exceeds the configured Query size limit.",
                    "Publish a bounded artifact or increase the owner-approved limit after capacity review.");
            }

            await boundedCopy.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        boundedCopy.Position = 0;
        try
        {
            var artifact = await JsonSerializer.DeserializeAsync<CatalogPublicationArtifact>(
                boundedCopy,
                SerializerOptions,
                cancellationToken);
            return artifact ?? throw Failure(
                "QUERY_PUBLICATION_ARTIFACT_EMPTY",
                $"Catalog publication artifact '{objectKey}' deserialized to no contract value.",
                "Republish the exact Catalog artifact after correcting its serialization.");
        }
        catch (JsonException exception)
        {
            throw Failure(
                "QUERY_PUBLICATION_ARTIFACT_INVALID",
                $"Catalog publication artifact '{objectKey}' does not satisfy the supported JSON contract.",
                "Correct the Catalog publication artifact and publish a new activation event.",
                exception);
        }
    }

    private void ValidateObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey) ||
            !objectKey.StartsWith(_options.AllowedObjectPrefix, StringComparison.Ordinal) ||
            objectKey.Contains("..", StringComparison.Ordinal) ||
            objectKey.StartsWith('/') ||
            objectKey.Contains('\\'))
        {
            throw Failure(
                "QUERY_PUBLICATION_ARTIFACT_KEY_INVALID",
                "Catalog publication event contains an object key outside the Query read scope.",
                "Publish an exact Catalog-owned object key within the configured prefix.");
        }
    }

    private static void ValidateDigest(string expectedDigest)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest) ||
            expectedDigest.Length != 64 ||
            expectedDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                "QUERY_PUBLICATION_ARTIFACT_DIGEST_INVALID",
                "Catalog publication event contains an invalid artifact digest.",
                "Correct the producer event before reading object storage.");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static QueryProjectionException Failure(
        string code,
        string message,
        string requiredAction,
        Exception? innerException = null) =>
        new(
            "Query.PublicationArtifact",
            code,
            422,
            message,
            requiredAction,
            innerException: innerException);
}
