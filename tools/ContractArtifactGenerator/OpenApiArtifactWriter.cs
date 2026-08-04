using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

internal static class OpenApiArtifactWriter
{
    public static object Build(
        ApiAssembly descriptor,
        Assembly assembly,
        JsonSchemaArtifactWriter _)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assembly);
        var schemas = new OpenApiSchemaRegistry();
        var paths = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var controller in assembly.GetExportedTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type))
            .Where(type => !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var controllerRoute = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(attribute => attribute.Template)
                .FirstOrDefault() ?? string.Empty;
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(method => method.MetadataToken))
            {
                var http = method.GetCustomAttributes(inherit: true)
                    .OfType<HttpMethodAttribute>()
                    .SingleOrDefault();
                if (http is null) continue;
                if (http.HttpMethods.Count != 1)
                    throw new InvalidOperationException(
                        $"Operation '{controller.FullName}.{method.Name}' must own exactly one HTTP method.");
                var route = CombineRoute(controllerRoute, http.Template, controller.Name);
                var verb = http.HttpMethods[0].ToLowerInvariant();
                var operationId = http.Name ?? method.Name;
                if (!operationIds.Add(operationId))
                    throw new InvalidOperationException($"Duplicate OpenAPI operationId '{operationId}'.");
                var pathItem = paths.TryGetValue(route, out var existing)
                    ? (Dictionary<string, object?>)existing!
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                pathItem[verb] = BuildOperation(controller, method, operationId, route, schemas);
                paths[route] = pathItem;
            }
        }
        var components = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["schemas"] = schemas.Components,
        };
        if (descriptor.Audience is not null)
        {
            components["securitySchemes"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["oidc"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "openIdConnect",
                    ["openIdConnectUrl"] = "/.well-known/openid-configuration",
                    ["description"] = $"OIDC token for audience '{descriptor.Audience}'.",
                },
            };
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["openapi"] = "3.1.0",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = $"Aggregator {descriptor.Name} API",
                ["version"] = "1.0.0",
            },
            ["servers"] = new object[] { new Dictionary<string, object?> { ["url"] = "/" } },
            ["paths"] = paths,
            ["components"] = components,
        };
    }

    private static Dictionary<string, object?> BuildOperation(
        Type controller,
        MethodInfo method,
        string operationId,
        string route,
        OpenApiSchemaRegistry schemas)
    {
        var operation = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["operationId"] = operationId,
            ["tags"] = new[] { controller.Name.Replace("Controller", string.Empty, StringComparison.Ordinal) },
            ["responses"] = BuildResponses(method, schemas),
        };
        var parameters = new List<object?>();
        Dictionary<string, object?>? requestBody = null;
        foreach (var parameter in method.GetParameters())
        {
            if (parameter.ParameterType == typeof(CancellationToken)) continue;
            var source = BindingSource(parameter, method);
            if (source == "body")
            {
                if (requestBody is not null)
                    throw new InvalidOperationException($"Operation '{operationId}' has more than one request body.");
                requestBody = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["required"] = !IsNullable(parameter),
                    ["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["application/json"] = new Dictionary<string, object?>
                        {
                            ["schema"] = schemas.Schema(parameter.ParameterType),
                        },
                    },
                };
                continue;
            }
            var name = ParameterName(parameter, source);
            var item = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = name,
                ["in"] = source,
                ["required"] = source == "path" || !IsNullable(parameter),
                ["schema"] = schemas.Schema(parameter.ParameterType),
            };
            if (parameter.HasDefaultValue && parameter.DefaultValue is not null)
            {
                ((Dictionary<string, object?>)item["schema"]!)["default"] = parameter.DefaultValue;
            }
            parameters.Add(item);
        }
        foreach (var routeParameter in RouteParameters(route))
        {
            if (!parameters.OfType<Dictionary<string, object?>>().Any(item =>
                string.Equals(item["name"]?.ToString(), routeParameter, StringComparison.Ordinal) &&
                string.Equals(item["in"]?.ToString(), "path", StringComparison.Ordinal)))
            {
                throw new InvalidOperationException(
                    $"Operation '{operationId}' route parameter '{routeParameter}' has no bound method parameter.");
            }
        }
        if (parameters.Count > 0) operation["parameters"] = parameters;
        if (requestBody is not null) operation["requestBody"] = requestBody;
        var policies = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .Concat(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
            .Select(attribute => attribute.Policy)
            .Where(policy => !string.IsNullOrWhiteSpace(policy))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var anonymous = controller.IsDefined(typeof(AllowAnonymousAttribute), inherit: true) ||
            method.IsDefined(typeof(AllowAnonymousAttribute), inherit: true);
        if (!anonymous && policies.Length > 0)
        {
            operation["security"] = new object[]
            {
                new Dictionary<string, object?> { ["oidc"] = policies },
            };
        }
        return operation;
    }

    private static SortedDictionary<string, object?> BuildResponses(
        MethodInfo method,
        OpenApiSchemaRegistry schemas)
    {
        var responses = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        var declared = method.GetCustomAttributes<ProducesResponseTypeAttribute>(inherit: true).ToArray();
        foreach (var response in declared)
        {
            var status = response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var value = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = ReasonPhrase(response.StatusCode),
            };
            var type = response.Type;
            if (type is not null && type != typeof(void) && type != typeof(IActionResult))
            {
                value["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = schemas.Schema(UnwrapResponse(type)),
                    },
                };
            }
            responses[status] = value;
        }
        if (responses.Count == 0)
        {
            var type = UnwrapResponse(method.ReturnType);
            var value = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["description"] = "Success",
            };
            if (type != typeof(void) && type != typeof(IActionResult))
            {
                value["content"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["application/json"] = new Dictionary<string, object?>
                    {
                        ["schema"] = schemas.Schema(type),
                    },
                };
            }
            responses["200"] = value;
        }
        return responses;
    }

    private static string BindingSource(ParameterInfo parameter, MethodInfo method)
    {
        if (parameter.IsDefined(typeof(FromRouteAttribute), inherit: true)) return "path";
        if (parameter.IsDefined(typeof(FromQueryAttribute), inherit: true)) return "query";
        if (parameter.IsDefined(typeof(FromHeaderAttribute), inherit: true)) return "header";
        if (parameter.IsDefined(typeof(FromBodyAttribute), inherit: true)) return "body";
        var route = method.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>()
            .Single().Template ?? string.Empty;
        if (RouteParameters(route).Contains(parameter.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            return "path";
        return ContractTypeRules.IsSimple(parameter.ParameterType) ? "query" : "body";
    }

    private static string ParameterName(ParameterInfo parameter, string source) => source switch
    {
        "path" => parameter.GetCustomAttribute<FromRouteAttribute>()?.Name ?? parameter.Name!,
        "query" => parameter.GetCustomAttribute<FromQueryAttribute>()?.Name ?? parameter.Name!,
        "header" => parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name ?? parameter.Name!,
        _ => parameter.Name!,
    };

    private static bool IsNullable(ParameterInfo parameter)
    {
        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null) return true;
        if (parameter.ParameterType.IsValueType) return false;
        return new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;
    }

    private static Type UnwrapResponse(Type type)
    {
        while (true)
        {
            if (type.IsGenericType &&
                (type.GetGenericTypeDefinition() == typeof(Task<>) ||
                 type.GetGenericTypeDefinition() == typeof(ValueTask<>) ||
                 type.GetGenericTypeDefinition() == typeof(ActionResult<>)))
            {
                type = type.GetGenericArguments()[0];
                continue;
            }
            return type == typeof(Task) || type == typeof(ValueTask) ? typeof(void) : type;
        }
    }

    private static string CombineRoute(string controllerRoute, string? actionRoute, string controllerName)
    {
        var combined = string.Join(
            '/',
            new[] { controllerRoute, actionRoute }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim('/')));
        combined = combined.Replace(
            "[controller]",
            controllerName.Replace("Controller", string.Empty, StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);
        if (combined.StartsWith("~/", StringComparison.Ordinal)) combined = combined[2..];
        var normalized = "/" + combined.Trim('/');
        return System.Text.RegularExpressions.Regex.Replace(
            normalized,
            "\\{(\\*\\*)?([^}:?]+)(:[^}?]+)?\\??\\}",
            match => "{" + match.Groups[2].Value + "}");
    }

    private static IReadOnlyList<string> RouteParameters(string route) =>
        System.Text.RegularExpressions.Regex.Matches(route, "\\{([^}]+)\\}")
            .Select(match => match.Groups[1].Value)
            .ToArray();

    private static string ReasonPhrase(int statusCode) => statusCode switch
    {
        200 => "Success",
        201 => "Created",
        202 => "Accepted",
        204 => "No Content",
        304 => "Not Modified",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => $"HTTP {statusCode}",
    };

    private sealed class OpenApiSchemaRegistry
    {
        private readonly NullabilityInfoContext _nullability = new();
        private readonly SortedDictionary<string, object?> _components = new(StringComparer.Ordinal);
        private readonly HashSet<Type> _building = [];

        public IReadOnlyDictionary<string, object?> Components => _components;

        public Dictionary<string, object?> Schema(Type type)
        {
            var nullable = Nullable.GetUnderlyingType(type) is not null;
            var actual = Nullable.GetUnderlyingType(type) ?? type;
            var schema = NonNullable(actual);
            if (nullable) schema["nullable"] = true;
            return schema;
        }

        private Dictionary<string, object?> NonNullable(Type type)
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
            if (type.IsEnum)
                return new Dictionary<string, object?>
                {
                    ["type"] = "string",
                    ["enum"] = Enum.GetNames(type).Select(ContractTypeRules.EnumToken).ToArray(),
                };
            var map = ContractTypeRules.DictionaryTypes(type);
            if (map is { } dictionary)
            {
                if (dictionary.Key != typeof(string)) throw new InvalidOperationException($"OpenAPI map '{type}' requires string keys.");
                return new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = Schema(dictionary.Value),
                };
            }
            var element = ContractTypeRules.CollectionElement(type);
            if (element is not null)
                return new Dictionary<string, object?> { ["type"] = "array", ["items"] = Schema(element) };
            if (!ContractTypeRules.IsContractType(type))
                throw new InvalidOperationException($"Unsupported OpenAPI contract type '{type.FullName}'.");
            var key = ContractTypeRules.SchemaKey(type);
            if (!_components.ContainsKey(key) && _building.Add(type))
            {
                var properties = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                var required = new List<string>();
                foreach (var property in ContractTypeRules.SerializableProperties(type))
                {
                    var name = ContractTypeRules.JsonName(property);
                    var propertySchema = Schema(property.PropertyType);
                    var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                        !property.PropertyType.IsValueType && _nullability.Create(property).ReadState == NullabilityState.Nullable;
                    if (nullable) propertySchema["nullable"] = true; else required.Add(name);
                    properties[name] = propertySchema;
                }
                var definition = new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = properties,
                };
                if (required.Count > 0) definition["required"] = required;
                _components[key] = definition;
                _building.Remove(type);
            }
            return new Dictionary<string, object?> { ["$ref"] = $"#/components/schemas/{key}" };
        }

        private static Dictionary<string, object?> TypeSchema(string type, string? format = null)
        {
            var schema = new Dictionary<string, object?> { ["type"] = type };
            if (format is not null) schema["format"] = format;
            return schema;
        }
    }
}
