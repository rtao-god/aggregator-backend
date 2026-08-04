using System.Reflection;
using Aggregator.CatalogMedia.Application;
using Platform.ObjectStorage;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var descriptorType = typeof(StoredObjectDescriptor);
var uploadType = typeof(CatalogMediaUploadAuthorization);
var objectStoreType = typeof(IObjectStore);

var descriptor = new DescriptorContract(
    FindProperty(descriptorType, typeof(string), ["Key", "ObjectKey"]),
    FindProperty(descriptorType, typeof(string), ["ContentType", "MediaType"]),
    FindProperty(descriptorType, typeof(string), ["Sha256", "Digest", "ContentDigest"]),
    FindNumericProperty(descriptorType, ["Length", "Size", "ContentLength"]));
var upload = new UploadContract(
    FindUriProperty(uploadType, ["UploadUri", "Uri", "Url"]),
    FindProperty(uploadType, typeof(DateTimeOffset), ["ExpiresAtUtc", "ExpiresAt"]),
    FindDictionaryProperty(uploadType, ["RequiredHeaders", "Headers"]));
var store = new ObjectStoreContract(
    RequireMethod(objectStoreType, "CreateScopedWriteUrlAsync"),
    RequireMethod(objectStoreType, "HeadAsync"),
    RequireMethod(objectStoreType, "OpenReadVerifiedAsync"),
    RequireMethod(objectStoreType, "PutVerifiedAsync"),
    RequireMethod(objectStoreType, "DeleteAsync"));

var context = new CatalogMediaGenerationContext(root, descriptor, upload, store);
InfrastructureTemplateWriter.Write(context);
NormalizeObjectStoreAdapter(context.InfrastructureDirectory);
MigrationTemplateWriter.Write(context);
ApiTemplateWriter.Write(context);
NormalizeApiConfigurationContract(context.ApiDirectory);
WorkerTemplateWriter.Write(context);
TestTemplateWriter.Write(context);

var reportDirectory = Path.Combine(root.FullName, "docs", "generated");
Directory.CreateDirectory(reportDirectory);
File.WriteAllText(
    Path.Combine(reportDirectory, "catalog-media-runtime-generation.md"),
    $"""
    # Catalog media runtime generation

    - Object metadata key: `StoredObjectDescriptor.{descriptor.Key.Name}`.
    - Object metadata content type: `StoredObjectDescriptor.{descriptor.ContentType.Name}`.
    - Object metadata digest: `StoredObjectDescriptor.{descriptor.Digest.Name}`.
    - Object metadata size: `StoredObjectDescriptor.{descriptor.Size.Name}`.
    - Upload response URI: `CatalogMediaUploadAuthorization.{upload.Uri.Name}`.
    - Upload response expiry: `CatalogMediaUploadAuthorization.{upload.ExpiresAt.Name}`.
    - Upload response required headers: `CatalogMediaUploadAuthorization.{upload.Headers.Name}`.
    - Object-store upload method: `IObjectStore.{store.CreateUpload.Name}`.
    - Media state, variants, commands, processing leases and outbox are persisted in `catalog_db`.
    - Publication insertion is blocked unless every referenced media asset is accepted and rights-active.
    """ + Environment.NewLine);

static void NormalizeObjectStoreAdapter(string infrastructureDirectory)
{
    var path = Path.Combine(infrastructureDirectory, "ObjectStoreCatalogMediaStore.cs");
    var source = File.ReadAllText(path);
    const string oldSource = """
                var upload = await objectStore.CreatePresignedUploadAsync(
                    asset.QuarantineObjectKey,
                    asset.ExpectedContentType,
                    asset.ExpectedSize,
                    lifetime,
                    cancellationToken);
                return new CatalogMediaUploadAuthorization(
                    upload.UploadUri,
                    upload.ExpiresAtUtc,
                    upload.RequiredHeaders);
        """;
    const string newSource = """
                var upload = await objectStore.CreateScopedWriteUrlAsync(
                    asset.QuarantineObjectKey,
                    asset.ExpectedContentType,
                    lifetime,
                    cancellationToken);
                return new CatalogMediaUploadAuthorization(
                    upload.Url,
                    upload.ExpiresAtUtc,
                    new Dictionary<string, string>(StringComparer.Ordinal));
        """;
    if (!source.Contains(oldSource, StringComparison.Ordinal))
    {
        throw Failure(
            "Generated Catalog media object-store adapter no longer matches the expected upload contract anchor.");
    }

    File.WriteAllText(path, source.Replace(oldSource, newSource, StringComparison.Ordinal));
}

static void NormalizeApiConfigurationContract(string apiDirectory)
{
    var path = Path.Combine(apiDirectory, "Program.cs");
    var source = File.ReadAllText(path);
    const string oldSource =
        "private static string Require(IConfiguration configuration, string path)";
    const string newSource =
        "private static string Require(Microsoft.Extensions.Configuration.ConfigurationManager configuration, string path)";
    if (!source.Contains(oldSource, StringComparison.Ordinal))
    {
        throw Failure(
            "Generated Catalog media API no longer matches the expected configuration contract anchor.");
    }

    File.WriteAllText(path, source.Replace(oldSource, newSource, StringComparison.Ordinal));
}

static PropertyInfo FindProperty(Type type, Type propertyType, IReadOnlyList<string> preferredNames)
{
    var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => propertyType.IsAssignableFrom(property.PropertyType))
        .ToArray();
    foreach (var preferred in preferredNames)
    {
        var exact = properties.FirstOrDefault(property =>
            property.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }
    }

    var fuzzy = properties.FirstOrDefault(property => preferredNames.Any(preferred =>
        property.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase)));
    return fuzzy ?? throw Failure(
        $"Type '{type.FullName}' has no required {propertyType.Name} property: {string.Join(", ", preferredNames)}.");
}

static PropertyInfo FindNumericProperty(Type type, IReadOnlyList<string> preferredNames)
{
    var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.PropertyType is not null)
        .Where(property => property.PropertyType == typeof(long) ||
            property.PropertyType == typeof(int) ||
            property.PropertyType == typeof(ulong) ||
            property.PropertyType == typeof(uint))
        .ToArray();
    foreach (var preferred in preferredNames)
    {
        var exact = properties.FirstOrDefault(property =>
            property.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }
    }

    return properties.FirstOrDefault(property => preferredNames.Any(preferred =>
        property.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase)))
        ?? throw Failure($"Type '{type.FullName}' has no numeric object-size property.");
}

static PropertyInfo FindUriProperty(Type type, IReadOnlyList<string> preferredNames)
{
    var uri = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .FirstOrDefault(property => property.PropertyType == typeof(Uri));
    return uri ?? FindProperty(type, typeof(string), preferredNames);
}

static PropertyInfo FindDictionaryProperty(Type type, IReadOnlyList<string> preferredNames)
{
    var candidates = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(property => property.PropertyType.GetInterfaces().Append(property.PropertyType).Any(candidate =>
            candidate.IsGenericType &&
            candidate.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>)) &&
            candidate.GetGenericArguments()[0] == typeof(string) &&
            candidate.GetGenericArguments()[1] == typeof(string)))
        .ToArray();
    foreach (var preferred in preferredNames)
    {
        var exact = candidates.FirstOrDefault(property =>
            property.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }
    }

    return candidates.FirstOrDefault()
        ?? throw Failure($"Type '{type.FullName}' has no string header dictionary property.");
}

static MethodInfo RequireMethod(Type type, string methodName) =>
    type.GetMethods().SingleOrDefault(method => method.Name == methodName)
    ?? throw Failure($"Object-store contract method '{type.FullName}.{methodName}' is unavailable.");

static DirectoryInfo FindRepositoryRoot(string start)
{
    var current = new DirectoryInfo(start);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "AggregatorBackend.slnx")))
        {
            return current;
        }

        current = current.Parent;
    }

    throw Failure("Repository root was not found.");
}

static InvalidOperationException Failure(string message) => new(message);

internal sealed record DescriptorContract(
    PropertyInfo Key,
    PropertyInfo ContentType,
    PropertyInfo Digest,
    PropertyInfo Size);

internal sealed record UploadContract(
    PropertyInfo Uri,
    PropertyInfo ExpiresAt,
    PropertyInfo Headers);

internal sealed record ObjectStoreContract(
    MethodInfo CreateUpload,
    MethodInfo Head,
    MethodInfo OpenRead,
    MethodInfo Put,
    MethodInfo Delete);

internal sealed record CatalogMediaGenerationContext(
    DirectoryInfo Root,
    DescriptorContract Descriptor,
    UploadContract Upload,
    ObjectStoreContract ObjectStore)
{
    public string InfrastructureDirectory =>
        Path.Combine(Root.FullName, "src", "Catalog", "Catalog.Media.Infrastructure");

    public string ApiDirectory =>
        Path.Combine(Root.FullName, "src", "Catalog", "Catalog.Media.Api");

    public string WorkerDirectory =>
        Path.Combine(Root.FullName, "src", "Catalog", "Catalog.Media.Worker");

    public string MigrationDirectory =>
        Path.Combine(Root.FullName, "src", "Catalog", "Catalog.Media.Migrations");

    public string TestsDirectory(string owner) =>
        Path.Combine(Root.FullName, "tests", "Catalog", $"Catalog.Media.{owner}.Tests");
}