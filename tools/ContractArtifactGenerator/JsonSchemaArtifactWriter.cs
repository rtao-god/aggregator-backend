using System.Reflection;
using System.Text.Json;

internal sealed class JsonSchemaArtifactWriter(string outputDirectory)
{
    private readonly NullabilityInfoContext _nullability = new();
    private readonly Dictionary<Type, Dictionary<string, object?>> _definitions = [];

    public void WriteRoot(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        _definitions.Clear();
        var root = Build(type, nullable: false);
        var document = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = $"urn:aggregator:{type.Assembly.GetName().Name}:{type.FullName}",
            ["title"] = type.Name,
        };
        foreach (var item in root) document[item.Key] = item.Value;
        if (_definitions.Count > 0)
        {
            document["$defs"] = _definitions
                .OrderBy(item => ContractTypeRules.SchemaKey(item.Key), StringComparer.Ordinal)
                .ToDictionary(
                    item => ContractTypeRules.SchemaKey(item.Key),
                    item => (object?)item.Value,
                    StringComparer.Ordinal);
        }
        var assemblyDirectory = Path.Combine(
            outputDirectory,
            type.Assembly.GetName().Name ?? "contracts");
        Directory.CreateDirectory(assemblyDirectory);
        File.WriteAllText(
            Path.Combine(assemblyDirectory, $"{type.Name}.schema.json"),
            JsonSerializer.Serialize(document, ArtifactJson.Options) + Environment.NewLine);
    }

    public Dictionary<string, object?> BuildOpenApiSchema(Type type) =>
        Build(type, nullable: IsNullableType(type));

    public IReadOnlyDictionary<Type, Dictionary<string, object?>> Definitions => _definitions;

    private Dictionary<string, object?> Build(Type input, bool nullable)
    {
        var underlying = Nullable.GetUnderlyingType(input);
        var type = underlying ?? input;
        var schema = BuildNonNullable(type);
        if (!nullable && underlying is null) return schema;
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["anyOf"] = new object?[]
            {
                schema,
                new Dictionary<string, object?> { ["type"] = "null" },
            },
        };
    }

    private Dictionary<string, object?> BuildNonNullable(Type type)
    {
        if (type == typeof(string)) return TypeSchema("string");
        if (type == typeof(bool)) return TypeSchema("boolean");
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long))
            return TypeSchema("integer", type == typeof(long) ? "int64" : "int32");
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return TypeSchema("number", type == typeof(float) ? "float" : type == typeof(double) ? "double" : "decimal");
        if (type == typeof(Guid)) return TypeSchema("string", "uuid");
        if (type == typeof(Uri)) return TypeSchema("string", "uri");
        if (type == typeof(DateOnly)) return TypeSchema("string", "date");
        if (type == typeof(TimeOnly)) return TypeSchema("string", "time");
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return TypeSchema("string", "date-time");
        if (type == typeof(byte[])) return TypeSchema("string", "byte");
        if (type == typeof(JsonElement))
        {
            throw new InvalidOperationException(
                "JsonElement is not allowed in a generated public contract without an explicit schema owner.");
        }
        if (type.IsEnum)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "string",
                ["enum"] = Enum.GetNames(type).Select(ContractTypeRules.EnumToken).ToArray(),
            };
        }

        var dictionary = ContractTypeRules.DictionaryTypes(type);
        if (dictionary is { } map)
        {
            if (map.Key != typeof(string))
                throw new InvalidOperationException($"Dictionary contract '{type}' must use string keys.");
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["additionalProperties"] = Build(map.Value, IsNullableType(map.Value)),
            };
        }
        var element = ContractTypeRules.CollectionElement(type);
        if (element is not null)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "array",
                ["items"] = Build(element, IsNullableType(element)),
            };
        }
        if (!ContractTypeRules.IsContractType(type))
        {
            throw new InvalidOperationException($"Unsupported public contract type '{type.FullName}'.");
        }

        var key = ContractTypeRules.SchemaKey(type);
        if (!_definitions.ContainsKey(type))
        {
            _definitions[type] = new Dictionary<string, object?>();
            _definitions[type] = BuildObjectDefinition(type);
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["$ref"] = $"#/$defs/{key}",
        };
    }

    private Dictionary<string, object?> BuildObjectDefinition(Type type)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();
        foreach (var property in ContractTypeRules.SerializableProperties(type))
        {
            var nullability = _nullability.Create(property);
            var nullable = IsNullable(property.PropertyType, nullability.ReadState);
            var name = ContractTypeRules.JsonName(property);
            properties[name] = Build(property.PropertyType, nullable);
            if (!nullable) required.Add(name);
        }
        var schema = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = properties,
        };
        if (required.Count > 0) schema["required"] = required.Order(StringComparer.Ordinal).ToArray();
        return schema;
    }

    private static bool IsNullable(Type type, NullabilityState state) =>
        Nullable.GetUnderlyingType(type) is not null ||
        !type.IsValueType && state == NullabilityState.Nullable;

    private static bool IsNullableType(Type type) => Nullable.GetUnderlyingType(type) is not null;

    private static Dictionary<string, object?> TypeSchema(string type, string? format = null)
    {
        var schema = new Dictionary<string, object?>(StringComparer.Ordinal) { ["type"] = type };
        if (format is not null) schema["format"] = format;
        return schema;
    }
}
