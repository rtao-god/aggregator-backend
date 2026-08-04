using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var root = FindRepositoryRoot(AppContext.BaseDirectory);
var output = Path.Combine(root.FullName, "contracts", "generated");
if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
Directory.CreateDirectory(output);

var apiAssemblies = new[]
{
    new ApiAssembly("catalog-command", "Catalog.Api", "aggregator-catalog-command"),
    new ApiAssembly("catalog-media", "Catalog.Media.Api", "aggregator-catalog-media"),
    new ApiAssembly("catalog-query", "Query.Api", null),
    new ApiAssembly("ingestion", "Ingestion.Api", "aggregator-ingestion"),
    new ApiAssembly("analytics", "Analytics.Api", "aggregator-analytics"),
    new ApiAssembly("promotion", "Promotion.Api", "aggregator-promotion"),
};
var contractAssemblyNames = new[]
{
    "Catalog.Contracts",
    "Catalog.Media.Contracts",
    "Query.Contracts",
    "Ingestion.Contracts",
    "Analytics.Contracts",
    "Promotion.Contracts",
};
var contracts = contractAssemblyNames.Select(Assembly.Load).ToArray();
var schemaWriter = new JsonSchemaArtifactWriter(Path.Combine(output, "json-schema"));
foreach (var assembly in contracts)
{
    foreach (var type in assembly.GetExportedTypes()
        .Where(ContractTypeRules.IsContractType)
        .OrderBy(type => type.FullName, StringComparer.Ordinal))
    {
        schemaWriter.WriteRoot(type);
    }
}

var openApiDirectory = Path.Combine(output, "openapi");
var clientDirectory = Path.Combine(output, "typescript");
Directory.CreateDirectory(openApiDirectory);
Directory.CreateDirectory(clientDirectory);
foreach (var descriptor in apiAssemblies)
{
    var assembly = Assembly.Load(descriptor.AssemblyName);
    var document = OpenApiArtifactWriter.Build(descriptor, assembly, schemaWriter);
    WriteJson(Path.Combine(openApiDirectory, $"{descriptor.Name}.openapi.json"), document);
    File.WriteAllText(
        Path.Combine(clientDirectory, $"{descriptor.Name}.client.ts"),
        TypeScriptClientWriter.Build(descriptor, assembly),
        Encoding.UTF8);
}

var asyncApi = AsyncApiArtifactWriter.Build(contracts);
WriteJson(Path.Combine(output, "asyncapi", "integration-events.asyncapi.json"), asyncApi);

var manifestEntries = Directory.GetFiles(output, "*", SearchOption.AllDirectories)
    .Where(path => !path.EndsWith("manifest.json", StringComparison.Ordinal))
    .Order(StringComparer.Ordinal)
    .Select(path => new
    {
        path = Path.GetRelativePath(output, path).Replace('\\', '/'),
        sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        size = new FileInfo(path).Length,
    })
    .ToArray();
WriteJson(Path.Combine(output, "manifest.json"), new
{
    contractIdentity = "aggregator-backend-contract-manifest",
    contractRevision = 1,
    generatedFromCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local",
    entries = manifestEntries,
});

static void WriteJson(string path, object value)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(
        path,
        JsonSerializer.Serialize(value, ArtifactJson.Options) + Environment.NewLine,
        Encoding.UTF8);
}

static DirectoryInfo FindRepositoryRoot(string start)
{
    var current = new DirectoryInfo(start);
    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "AggregatorBackend.slnx"))) return current;
        current = current.Parent;
    }
    throw new InvalidOperationException("Repository root was not found.");
}

internal sealed record ApiAssembly(string Name, string AssemblyName, string? Audience);

internal static class ArtifactJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
