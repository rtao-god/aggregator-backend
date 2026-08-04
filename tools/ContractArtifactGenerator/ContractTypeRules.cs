using System.Reflection;
using System.Text.Json.Serialization;

internal static class ContractTypeRules
{
    public static bool IsContractType(Type type) =>
        type.IsPublic &&
        !type.IsNested &&
        !type.IsGenericTypeDefinition &&
        !type.IsAbstract &&
        !type.IsInterface &&
        !type.IsDefined(typeof(JsonIgnoreAttribute), inherit: true) &&
        (type.IsEnum || type.IsClass || type.IsValueType) &&
        !type.Name.EndsWith("Attribute", StringComparison.Ordinal) &&
        !type.Name.EndsWith("Exception", StringComparison.Ordinal);

    public static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsEnum ||
            type == typeof(string) ||
            type == typeof(bool) ||
            type == typeof(byte) ||
            type == typeof(short) ||
            type == typeof(int) ||
            type == typeof(long) ||
            type == typeof(float) ||
            type == typeof(double) ||
            type == typeof(decimal) ||
            type == typeof(Guid) ||
            type == typeof(Uri) ||
            type == typeof(DateOnly) ||
            type == typeof(TimeOnly) ||
            type == typeof(DateTime) ||
            type == typeof(DateTimeOffset) ||
            type == typeof(JsonElement);
    }

    public static Type? CollectionElement(Type type)
    {
        if (type == typeof(string) || type == typeof(byte[])) return null;
        if (type.IsArray) return type.GetElementType();
        return type.GetInterfaces().Append(type)
            .Where(candidate => candidate.IsGenericType)
            .Where(candidate => candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(candidate => candidate.GetGenericArguments()[0])
            .FirstOrDefault();
    }

    public static (Type Key, Type Value)? DictionaryTypes(Type type)
    {
        return type.GetInterfaces().Append(type)
            .Where(candidate => candidate.IsGenericType)
            .Where(candidate => candidate.GetGenericTypeDefinition() is var definition &&
                (definition == typeof(IReadOnlyDictionary<,>) ||
                 definition == typeof(IDictionary<,>) ||
                 definition == typeof(Dictionary<,>)))
            .Select(candidate =>
            {
                var arguments = candidate.GetGenericArguments();
                return ((Type Key, Type Value)?)(arguments[0], arguments[1]);
            })
            .FirstOrDefault();
    }

    public static IReadOnlyList<PropertyInfo> SerializableProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod is not null)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => !property.IsDefined(typeof(JsonIgnoreAttribute), inherit: true))
            .OrderBy(property => JsonName(property), StringComparer.Ordinal)
            .ToArray();

    public static string JsonName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
        ?? JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    public static string SchemaKey(Type type) =>
        (type.FullName ?? type.Name)
            .Replace('+', '.')
            .Replace('.', '_')
            .Replace('`', '_');

    public static string EnumToken(string value) => JsonNamingPolicy.CamelCase.ConvertName(value);
}
