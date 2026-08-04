using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContractArtifacts;

internal static class Program
{
    private static readonly string[] ContractProjects =
    [
        "src/Catalog/Catalog.Contracts/Catalog.Contracts.csproj",
        "src/Query/Query.Contracts/Query.Contracts.csproj",
        "src/Ingestion/Ingestion.Contracts/Ingestion.Contracts.csproj",
        "src/Analytics/Analytics.Contracts/Analytics.Contracts.csproj",
        "src/Promotion/Promotion.Contracts/Promotion.Contracts.csproj",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        var write = args.Contains("--write", StringComparer.Ordinal);
        var verify = args.Contains("--verify", StringComparer.Ordinal) || !write;
        if (write && verify && args.Contains("--verify", StringComparer.Ordinal))
        {
            Console.Error.WriteLine("Choose exactly one mode: --write or --verify.");
            return 2;
        }

        var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        var failures = new List<string>();
        foreach (var project in ContractProjects)
        {
            var projectPath = Path.Combine(repositoryRoot, project.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(projectPath))
            {
                failures.Add($"Contract project is missing: {project}");
                continue;
            }

            var assemblyName = Path.GetFileNameWithoutExtension(projectPath);
            var assembly = Assembly.Load(new AssemblyName(assemblyName));
            var artifactDirectory = Path.Combine(
                repositoryRoot,
                "artifacts",
                "contracts",
                ToKebabCase(assemblyName));
            var artifacts = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["schema.json"] = GenerateSchema(assembly),
                ["contracts.ts"] = GenerateTypeScript(assembly),
            };

            foreach (var artifact in artifacts)
            {
                var outputPath = Path.Combine(artifactDirectory, artifact.Key);
                if (write)
                {
                    Directory.CreateDirectory(artifactDirectory);
                    File.WriteAllBytes(outputPath, artifact.Value);
                    Console.WriteLine($"Wrote {Path.GetRelativePath(repositoryRoot, outputPath)}");
                    continue;
                }

                if (!File.Exists(outputPath))
                {
                    failures.Add($"Generated artifact is missing: {Path.GetRelativePath(repositoryRoot, outputPath)}");
                    continue;
                }

                var existing = File.ReadAllBytes(outputPath);
                if (!existing.AsSpan().SequenceEqual(artifact.Value))
                {
                    failures.Add($"Generated artifact drifted: {Path.GetRelativePath(repositoryRoot, outputPath)}");
                }
            }
        }

        if (failures.Count == 0)
        {
            Console.WriteLine(write
                ? "Contract artifacts generated deterministically."
                : "Committed contract artifacts match producer-owned assemblies.");
            return 0;
        }

        foreach (var failure in failures.Order(StringComparer.Ordinal))
        {
            Console.Error.WriteLine(failure);
        }

        Console.Error.WriteLine("Run: dotnet run --project tools/ContractArtifacts -- --write");
        return 1;
    }

    private static byte[] GenerateSchema(Assembly assembly)
    {
        var contractTypes = GetContractTypes(assembly);
        var definitions = new JsonObject();
        foreach (var type in contractTypes)
        {
            definitions[DefinitionName(type)] = CreateObjectDefinition(type, assembly);
        }

        var document = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"https://contracts.aggregator.local/{ToKebabCase(assembly.GetName().Name!)}/schema.json",
            ["title"] = assembly.GetName().Name,
            ["type"] = "object",
            ["$defs"] = definitions,
        };
        return Utf8(document.ToJsonString(JsonOptions) + "\n");
    }

    private static JsonNode CreateObjectDefinition(Type type, Assembly ownerAssembly)
    {
        if (type.IsEnum)
        {
            return EnumSchema(type);
        }

        var properties = new JsonObject();
        var required = new JsonArray();
        var nullability = new NullabilityInfoContext();
        foreach (var property in type
                     .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                     .Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0)
                     .OrderBy(property => JsonName(property), StringComparer.Ordinal))
        {
            var propertyName = JsonName(property);
            var nullabilityInfo = nullability.Create(property);
            properties[propertyName] = SchemaFor(
                property.PropertyType,
                nullabilityInfo.ReadState,
                ownerAssembly);
            if (IsRequired(property.PropertyType, nullabilityInfo.ReadState))
            {
                required.Add(propertyName);
            }
        }

        var result = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
        };
        if (required.Count > 0)
        {
            result["required"] = required;
        }

        return result;
    }

    private static JsonNode SchemaFor(Type sourceType, NullabilityState nullability, Assembly ownerAssembly)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(sourceType);
        var type = nullableUnderlying ?? sourceType;
        var nullable = nullableUnderlying is not null ||
            (!type.IsValueType && nullability != NullabilityState.NotNull);
        var schema = NonNullableSchemaFor(type, ownerAssembly);
        if (!nullable)
        {
            return schema;
        }

        return new JsonObject
        {
            ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }),
        };
    }

    private static JsonNode NonNullableSchemaFor(Type type, Assembly ownerAssembly)
    {
        if (type == typeof(string) || type == typeof(char))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(bool))
        {
            return new JsonObject { ["type"] = "boolean" };
        }

        if (type == typeof(byte) || type == typeof(sbyte) ||
            type == typeof(short) || type == typeof(ushort) ||
            type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong))
        {
            return new JsonObject { ["type"] = "integer" };
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return new JsonObject { ["type"] = "number" };
        }

        if (type == typeof(Guid))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "uuid" };
        }

        if (type == typeof(DateTime) || type == typeof(DateTimeOffset))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
        }

        if (type == typeof(DateOnly))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "date" };
        }

        if (type == typeof(TimeOnly) || type == typeof(TimeSpan))
        {
            return new JsonObject { ["type"] = "string" };
        }

        if (type == typeof(Uri))
        {
            return new JsonObject { ["type"] = "string", ["format"] = "uri" };
        }

        if (type == typeof(byte[]) || type == typeof(ReadOnlyMemory<byte>) || type == typeof(Memory<byte>))
        {
            return new JsonObject
            {
                ["type"] = "string",
                ["contentEncoding"] = "base64",
            };
        }

        if (type == typeof(JsonElement) || type == typeof(JsonDocument) || type == typeof(object))
        {
            return new JsonObject();
        }

        if (type.IsEnum)
        {
            return type.Assembly == ownerAssembly
                ? RefSchema(type)
                : EnumSchema(type);
        }

        if (TryGetDictionaryValueType(type, out var dictionaryValueType))
        {
            return new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = SchemaFor(
                    dictionaryValueType,
                    dictionaryValueType.IsValueType ? NullabilityState.NotNull : NullabilityState.Nullable,
                    ownerAssembly),
            };
        }

        if (TryGetEnumerableElementType(type, out var elementType))
        {
            return new JsonObject
            {
                ["type"] = "array",
                ["items"] = SchemaFor(
                    elementType,
                    elementType.IsValueType ? NullabilityState.NotNull : NullabilityState.Nullable,
                    ownerAssembly),
            };
        }

        if (type.Assembly == ownerAssembly && type.IsPublic)
        {
            return RefSchema(type);
        }

        return new JsonObject { ["type"] = "object" };
    }

    private static JsonObject RefSchema(Type type) =>
        new() { ["$ref"] = $"#/$defs/{DefinitionName(type)}" };

    private static JsonObject EnumSchema(Type type)
    {
        var values = new JsonArray();
        foreach (var name in Enum.GetNames(type))
        {
            values.Add(ToCamelCase(name));
        }

        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = values,
        };
    }

    private static byte[] GenerateTypeScript(Assembly assembly)
    {
        var builder = new StringBuilder();
        builder.AppendLine("/* Generated by tools/ContractArtifacts. Do not edit manually. */");
        builder.AppendLine("/* Producer assembly: " + assembly.GetName().Name + " */");
        builder.AppendLine();
        foreach (var type in GetContractTypes(assembly))
        {
            if (type.IsEnum)
            {
                builder.Append("export type ").Append(TypeScriptName(type)).Append(" = ");
                builder.Append(string.Join(
                    " | ",
                    Enum.GetNames(type).Select(name => $"'{ToCamelCase(name)}'")));
                builder.AppendLine(";");
                builder.AppendLine();
                continue;
            }

            builder.Append("export interface ").Append(TypeScriptName(type)).AppendLine(" {");
            var nullability = new NullabilityInfoContext();
            foreach (var property in type
                         .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                         .Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0)
                         .OrderBy(property => JsonName(property), StringComparer.Ordinal))
            {
                var info = nullability.Create(property);
                var optional = IsRequired(property.PropertyType, info.ReadState) ? string.Empty : "?";
                builder.Append("  ")
                    .Append(JsonName(property))
                    .Append(optional)
                    .Append(": ")
                    .Append(TypeScriptType(property.PropertyType, info.ReadState, assembly))
                    .AppendLine(";");
            }

            builder.AppendLine("}");
            builder.AppendLine();
        }

        return Utf8(builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string TypeScriptType(Type sourceType, NullabilityState nullability, Assembly ownerAssembly)
    {
        var nullableUnderlying = Nullable.GetUnderlyingType(sourceType);
        var type = nullableUnderlying ?? sourceType;
        var nullable = nullableUnderlying is not null ||
            (!type.IsValueType && nullability != NullabilityState.NotNull);
        var value = NonNullableTypeScriptType(type, ownerAssembly);
        return nullable ? value + " | null" : value;
    }

    private static string NonNullableTypeScriptType(Type type, Assembly ownerAssembly)
    {
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) ||
            type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(TimeSpan) ||
            type == typeof(Uri) || type == typeof(byte[]) ||
            type == typeof(ReadOnlyMemory<byte>) || type == typeof(Memory<byte>))
        {
            return "string";
        }

        if (type == typeof(bool))
        {
            return "boolean";
        }

        if (type.IsPrimitive || type == typeof(decimal))
        {
            return "number";
        }

        if (type == typeof(JsonElement) || type == typeof(JsonDocument) || type == typeof(object))
        {
            return "unknown";
        }

        if (TryGetDictionaryValueType(type, out var dictionaryValueType))
        {
            return $"Record<string, {TypeScriptType(dictionaryValueType, NullabilityState.Nullable, ownerAssembly)}>";
        }

        if (TryGetEnumerableElementType(type, out var elementType))
        {
            return $"ReadonlyArray<{TypeScriptType(elementType, NullabilityState.Nullable, ownerAssembly)}>";
        }

        return type.Assembly == ownerAssembly ? TypeScriptName(type) : "unknown";
    }

    private static IReadOnlyList<Type> GetContractTypes(Assembly assembly) =>
        assembly.GetExportedTypes()
            .Where(type =>
                !type.IsGenericTypeDefinition &&
                !type.IsInterface &&
                !typeof(Delegate).IsAssignableFrom(type) &&
                !(type.IsAbstract && type.IsSealed) &&
                !type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) &&
                (type.IsEnum || type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Length > 0))
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .ToArray();

    private static bool TryGetEnumerableElementType(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return type != typeof(byte[]);
        }

        var enumerable = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null && type != typeof(string))
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }

        elementType = null!;
        return false;
    }

    private static bool TryGetDictionaryValueType(Type type, out Type valueType)
    {
        var dictionary = type
            .GetInterfaces()
            .Append(type)
            .FirstOrDefault(candidate =>
                candidate.IsGenericType &&
                (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)) &&
                candidate.GetGenericArguments()[0] == typeof(string));
        if (dictionary is not null)
        {
            valueType = dictionary.GetGenericArguments()[1];
            return true;
        }

        valueType = null!;
        return false;
    }

    private static bool IsRequired(Type type, NullabilityState state) =>
        Nullable.GetUnderlyingType(type) is null &&
        (type.IsValueType || state == NullabilityState.NotNull);

    private static string JsonName(PropertyInfo property)
    {
        var attribute = property.GetCustomAttributes(inherit: true)
            .FirstOrDefault(candidate => candidate.GetType().FullName == "System.Text.Json.Serialization.JsonPropertyNameAttribute");
        var explicitName = attribute?.GetType().GetProperty("Name")?.GetValue(attribute) as string;
        return explicitName ?? ToCamelCase(property.Name);
    }

    private static string DefinitionName(Type type) =>
        (type.FullName ?? type.Name)
            .Replace('+', '.')
            .Replace('`', '_');

    private static string TypeScriptName(Type type) =>
        type.Name.Split('`')[0].Replace('+', '_');

    private static string FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AggregatorBackend.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("AggregatorBackend.slnx was not found above the application directory.");
    }

    private static string ToCamelCase(string value) =>
        string.IsNullOrEmpty(value) || char.IsLower(value[0])
            ? value
            : char.ToLowerInvariant(value[0]) + value[1..];

    private static string ToKebabCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is '.' or '_' or ' ')
            {
                if (builder.Length > 0 && builder[^1] != '-')
                {
                    builder.Append('-');
                }

                continue;
            }

            if (char.IsUpper(character) && index > 0 && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static byte[] Utf8(string value) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(value);
}
