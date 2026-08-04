using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

internal static class TypeScriptClientWriter
{
    public static string Build(ApiAssembly descriptor, Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(assembly);
        var types = new SortedDictionary<string, Type>(StringComparer.Ordinal);
        var operations = new List<ClientOperation>();
        foreach (var controller in assembly.GetExportedTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            var baseRoute = controller.GetCustomAttributes<RouteAttribute>(inherit: true)
                .Select(attribute => attribute.Template)
                .FirstOrDefault() ?? string.Empty;
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .OrderBy(method => method.MetadataToken))
            {
                var http = method.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>().SingleOrDefault();
                if (http is null) continue;
                if (http.HttpMethods.Count != 1)
                    throw new InvalidOperationException($"Client operation '{method.Name}' must own exactly one HTTP verb.");
                var operationId = http.Name ?? method.Name;
                var route = NormalizeRoute(baseRoute, http.Template, controller.Name);
                var parameters = new List<ClientParameter>();
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType == typeof(CancellationToken)) continue;
                    var source = Source(parameter, method);
                    RegisterType(parameter.ParameterType, types);
                    parameters.Add(new ClientParameter(
                        parameter.Name ?? throw new InvalidOperationException("Client parameter name is unavailable."),
                        source,
                        parameter.ParameterType,
                        Nullable(parameter),
                        parameter.HasDefaultValue ? parameter.DefaultValue : null));
                }
                var response = SuccessResponse(method);
                if (response != typeof(void)) RegisterType(response, types);
                var policies = controller.GetCustomAttributes<AuthorizeAttribute>(inherit: true)
                    .Concat(method.GetCustomAttributes<AuthorizeAttribute>(inherit: true))
                    .Select(attribute => attribute.Policy)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                var anonymous = controller.IsDefined(typeof(AllowAnonymousAttribute), inherit: true) ||
                    method.IsDefined(typeof(AllowAnonymousAttribute), inherit: true);
                operations.Add(new ClientOperation(
                    operationId,
                    http.HttpMethods[0].ToUpperInvariant(),
                    route,
                    parameters,
                    response,
                    !anonymous && policies.Length > 0,
                    policies));
            }
        }

        var builder = new StringBuilder();
        builder.AppendLine("/* Generated from compiled ASP.NET contracts. Do not edit manually. */");
        builder.AppendLine("export interface ApiProblem {");
        builder.AppendLine("  type?: string;");
        builder.AppendLine("  title?: string;");
        builder.AppendLine("  status?: number;");
        builder.AppendLine("  detail?: string;");
        builder.AppendLine("  owner?: string;");
        builder.AppendLine("  code?: string;");
        builder.AppendLine("  correlationId?: string;");
        builder.AppendLine("  requiredAction?: string;");
        builder.AppendLine("  [extension: string]: unknown;");
        builder.AppendLine("}");
        builder.AppendLine();
        foreach (var type in types.Values)
        {
            WriteType(builder, type);
            builder.AppendLine();
        }
        builder.AppendLine("export class ApiError extends Error {");
        builder.AppendLine("  public constructor(public readonly status: number, public readonly problem: ApiProblem) {");
        builder.AppendLine("    super(problem.detail ?? problem.title ?? `HTTP ${status}`);");
        builder.AppendLine("  }");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine("export interface ClientOptions {");
        builder.AppendLine("  baseUrl?: string;");
        builder.AppendLine("  fetchImpl?: typeof fetch;");
        builder.AppendLine("  accessToken?: string;");
        builder.AppendLine("}");
        builder.AppendLine();
        builder.AppendLine($"export class {Pascal(descriptor.Name)}Client {{");
        builder.AppendLine("  private readonly baseUrl: string;");
        builder.AppendLine("  private readonly fetchImpl: typeof fetch;");
        builder.AppendLine("  private readonly accessToken?: string;");
        builder.AppendLine("  public constructor(options: ClientOptions = {}) {");
        builder.AppendLine("    this.baseUrl = (options.baseUrl ?? '').replace(/\/$/, '');");
        builder.AppendLine("    this.fetchImpl = options.fetchImpl ?? fetch;");
        builder.AppendLine("    this.accessToken = options.accessToken;");
        builder.AppendLine("  }");
        foreach (var operation in operations.OrderBy(item => item.OperationId, StringComparer.Ordinal))
        {
            WriteOperation(builder, operation);
        }
        builder.AppendLine("  private async request<T>(path: string, init: RequestInit): Promise<T> {");
        builder.AppendLine("    const response = await this.fetchImpl(`${this.baseUrl}${path}`, init);");
        builder.AppendLine("    if (!response.ok) {");
        builder.AppendLine("      const problem = await response.json().catch(() => ({ status: response.status })) as ApiProblem;");
        builder.AppendLine("      throw new ApiError(response.status, problem);");
        builder.AppendLine("    }");
        builder.AppendLine("    if (response.status === 204 || response.status === 304) return undefined as T;");
        builder.AppendLine("    return await response.json() as T;");
        builder.AppendLine("  }");
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static void WriteType(StringBuilder builder, Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (ContractTypeRules.IsSimple(type) || ContractTypeRules.CollectionElement(type) is not null ||
            ContractTypeRules.DictionaryTypes(type) is not null)
            return;
        if (type.IsEnum)
        {
            builder.Append("export type ").Append(type.Name).Append(" = ")
                .Append(string.Join(" | ", Enum.GetNames(type).Select(value => $"'{ContractTypeRules.EnumToken(value)}'")))
                .AppendLine(";");
            return;
        }
        if (!ContractTypeRules.IsContractType(type))
            throw new InvalidOperationException($"Unsupported TypeScript contract '{type.FullName}'.");
        var nullability = new NullabilityInfoContext();
        builder.Append("export interface ").Append(type.Name).AppendLine(" {");
        foreach (var property in ContractTypeRules.SerializableProperties(type))
        {
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                !property.PropertyType.IsValueType && nullability.Create(property).ReadState == NullabilityState.Nullable;
            builder.Append("  ").Append(ContractTypeRules.JsonName(property));
            if (nullable) builder.Append('?');
            builder.Append(": ").Append(TypeScriptType(property.PropertyType));
            if (nullable) builder.Append(" | null");
            builder.AppendLine(";");
        }
        builder.AppendLine("}");
    }

    private static void WriteOperation(StringBuilder builder, ClientOperation operation)
    {
        var parameters = operation.Parameters;
        builder.Append("  public async ").Append(Camel(operation.OperationId)).Append("(args: {");
        foreach (var parameter in parameters)
        {
            builder.Append(' ').Append(parameter.Name);
            if (parameter.Nullable || parameter.DefaultValue is not null) builder.Append('?');
            builder.Append(": ").Append(TypeScriptType(parameter.Type)).Append(';');
        }
        if (operation.RequiresToken) builder.Append(" accessToken?: string;");
        builder.Append(" signal?: AbortSignal; } = {}): Promise<")
            .Append(operation.ResponseType == typeof(void) ? "void" : TypeScriptType(operation.ResponseType))
            .AppendLine("> {");
        var route = operation.Route;
        foreach (var parameter in parameters.Where(item => item.Source == "path"))
        {
            route = route.Replace(
                "{" + parameter.Name + "}",
                $"${{encodeURIComponent(String(args.{parameter.Name}))}}",
                StringComparison.OrdinalIgnoreCase);
        }
        builder.Append("    let path = `").Append(route).AppendLine("`; ");
        var query = parameters.Where(item => item.Source == "query").ToArray();
        if (query.Length > 0)
        {
            builder.AppendLine("    const query = new URLSearchParams();");
            foreach (var parameter in query)
            {
                builder.Append("    if (args.").Append(parameter.Name).Append(" !== undefined && args.")
                    .Append(parameter.Name).Append(" !== null) query.set('")
                    .Append(parameter.Name).Append("', String(args.").Append(parameter.Name).AppendLine("));");
            }
            builder.AppendLine("    const queryString = query.toString();");
            builder.AppendLine("    if (queryString) path += `?${queryString}`;");
        }
        builder.AppendLine("    const headers: Record<string, string> = { 'Accept': 'application/json' };");
        var body = parameters.SingleOrDefault(item => item.Source == "body");
        if (body is not null)
        {
            builder.AppendLine("    headers['Content-Type'] = 'application/json';");
        }
        foreach (var header in parameters.Where(item => item.Source == "header"))
        {
            builder.Append("    if (args.").Append(header.Name).Append(" !== undefined) headers['")
                .Append(header.Name).Append("'] = String(args.").Append(header.Name).AppendLine(");");
        }
        if (operation.RequiresToken)
        {
            builder.AppendLine("    const token = args.accessToken ?? this.accessToken;");
            builder.Append("    if (!token) throw new Error('Access token is required for scopes: ")
                .Append(string.Join(' ', operation.Policies)).AppendLine("');");
            builder.AppendLine("    headers['Authorization'] = `Bearer ${token}`;");
        }
        builder.Append("    return await this.request<")
            .Append(operation.ResponseType == typeof(void) ? "void" : TypeScriptType(operation.ResponseType))
            .Append(">(path, { method: '").Append(operation.HttpMethod).Append("', headers, signal: args.signal");
        if (body is not null) builder.Append(", body: JSON.stringify(args.").Append(body.Name).Append(')');
        builder.AppendLine(" });");
        builder.AppendLine("  }");
    }

    private static string Source(ParameterInfo parameter, MethodInfo method)
    {
        if (parameter.IsDefined(typeof(FromRouteAttribute), true)) return "path";
        if (parameter.IsDefined(typeof(FromQueryAttribute), true)) return "query";
        if (parameter.IsDefined(typeof(FromHeaderAttribute), true)) return "header";
        if (parameter.IsDefined(typeof(FromBodyAttribute), true)) return "body";
        var route = method.GetCustomAttributes(true).OfType<HttpMethodAttribute>().Single().Template ?? string.Empty;
        return route.Contains("{" + parameter.Name, StringComparison.OrdinalIgnoreCase)
            ? "path"
            : ContractTypeRules.IsSimple(parameter.ParameterType) ? "query" : "body";
    }

    private static Type SuccessResponse(MethodInfo method)
    {
        var declared = method.GetCustomAttributes<ProducesResponseTypeAttribute>(true)
            .Where(attribute => attribute.StatusCode is >= 200 and < 300)
            .Where(attribute => attribute.Type is not null && attribute.Type != typeof(void))
            .Select(attribute => attribute.Type!)
            .Distinct()
            .ToArray();
        if (declared.Length > 1)
            throw new InvalidOperationException($"Operation '{method.Name}' has multiple incompatible success response types.");
        return declared.Length == 1 ? Unwrap(declared[0]) : Unwrap(method.ReturnType);
    }

    private static Type Unwrap(Type type)
    {
        while (type.IsGenericType &&
            (type.GetGenericTypeDefinition() == typeof(Task<>) ||
             type.GetGenericTypeDefinition() == typeof(ValueTask<>) ||
             type.GetGenericTypeDefinition() == typeof(ActionResult<>)))
            type = type.GetGenericArguments()[0];
        return type == typeof(Task) || type == typeof(ValueTask) || type == typeof(IActionResult)
            ? typeof(void)
            : type;
    }

    private static void RegisterType(Type input, IDictionary<string, Type> types)
    {
        var type = Nullable.GetUnderlyingType(input) ?? input;
        if (ContractTypeRules.IsSimple(type))
        {
            if (type.IsEnum) types[type.FullName ?? type.Name] = type;
            return;
        }
        var map = ContractTypeRules.DictionaryTypes(type);
        if (map is { } dictionary)
        {
            RegisterType(dictionary.Value, types);
            return;
        }
        var element = ContractTypeRules.CollectionElement(type);
        if (element is not null)
        {
            RegisterType(element, types);
            return;
        }
        if (!ContractTypeRules.IsContractType(type))
            throw new InvalidOperationException($"Unsupported TypeScript type '{type.FullName}'.");
        if (!types.TryAdd(type.FullName ?? type.Name, type)) return;
        foreach (var property in ContractTypeRules.SerializableProperties(type)) RegisterType(property.PropertyType, types);
    }

    private static string TypeScriptType(Type input)
    {
        var type = Nullable.GetUnderlyingType(input) ?? input;
        if (type == typeof(string) || type == typeof(Guid) || type == typeof(Uri) ||
            type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(DateTime) ||
            type == typeof(DateTimeOffset)) return "string";
        if (type == typeof(bool)) return "boolean";
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long) ||
            type == typeof(float) || type == typeof(double) || type == typeof(decimal)) return "number";
        if (type == typeof(byte[])) return "string";
        if (type.IsEnum || ContractTypeRules.IsContractType(type)) return type.Name;
        var map = ContractTypeRules.DictionaryTypes(type);
        if (map is { } dictionary)
        {
            if (dictionary.Key != typeof(string)) throw new InvalidOperationException("TypeScript maps require string keys.");
            return $"Record<string, {TypeScriptType(dictionary.Value)}>";
        }
        var element = ContractTypeRules.CollectionElement(type);
        if (element is not null) return $"ReadonlyArray<{TypeScriptType(element)}>";
        throw new InvalidOperationException($"No TypeScript mapping exists for '{type.FullName}'.");
    }

    private static bool Nullable(ParameterInfo parameter)
    {
        if (Nullable.GetUnderlyingType(parameter.ParameterType) is not null) return true;
        if (parameter.ParameterType.IsValueType) return false;
        return new NullabilityInfoContext().Create(parameter).ReadState == NullabilityState.Nullable;
    }

    private static string NormalizeRoute(string controllerRoute, string? actionRoute, string controllerName)
    {
        var action = actionRoute ?? string.Empty;
        if (action.StartsWith("~/", StringComparison.Ordinal)) controllerRoute = string.Empty;
        var route = string.Join('/', new[] { controllerRoute, action }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.TrimStart('~').Trim('/')));
        route = route.Replace("[controller]", controllerName.Replace("Controller", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        return "/" + System.Text.RegularExpressions.Regex.Replace(
            route.Trim('/'),
            "\\{(\\*\\*)?([^}:?]+)(:[^}?]+)?\\??\\}",
            match => "{" + match.Groups[2].Value + "}");
    }

    private static string Pascal(string value) => string.Concat(
        value.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static string Camel(string value)
    {
        var pascal = Pascal(value);
        return char.ToLowerInvariant(pascal[0]) + pascal[1..];
    }

    private sealed record ClientParameter(
        string Name,
        string Source,
        Type Type,
        bool Nullable,
        object? DefaultValue);

    private sealed record ClientOperation(
        string OperationId,
        string HttpMethod,
        string Route,
        IReadOnlyList<ClientParameter> Parameters,
        Type ResponseType,
        bool RequiresToken,
        IReadOnlyList<string> Policies);
}
