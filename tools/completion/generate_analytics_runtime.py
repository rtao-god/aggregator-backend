#!/usr/bin/env python3
"""Generate strongly typed Analytics API and migration owner projects from committed source contracts.

The generator is intentionally fail-closed. It emits no runtime-reflection bridge: every service
parameter must map to an HTTP value, a known owner context factory, or CancellationToken.
"""

from __future__ import annotations

import re
import shutil
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ANALYTICS = ROOT / "src" / "Analytics"
APPLICATION = ANALYTICS / "Analytics.Application"
CONTRACTS = ANALYTICS / "Analytics.Contracts"
INFRASTRUCTURE = ANALYTICS / "Analytics.Infrastructure"
API = ANALYTICS / "Analytics.Api"
MIGRATIONS = ANALYTICS / "Analytics.Migrations"
REPORT = ROOT / "docs" / "generated" / "analytics-runtime-generation.md"


@dataclass(frozen=True)
class Parameter:
    type_name: str
    name: str
    default_value: str | None


@dataclass(frozen=True)
class Method:
    service_name: str
    name: str
    return_type: str
    parameters: tuple[Parameter, ...]


def fail(message: str) -> "NoReturn":
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(
        "# Analytics runtime generation\n\n"
        "Generation failed closed.\n\n"
        f"```text\n{message}\n```\n",
        encoding="utf-8",
    )
    raise RuntimeError(message)


def write(path: Path, content: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content.rstrip() + "\n", encoding="utf-8")


def split_top_level(value: str) -> list[str]:
    result: list[str] = []
    start = 0
    depths = {"<": 0, "(": 0, "[": 0, "{": 0}
    pairs = {">": "<", ")": "(", "]": "[", "}": "{"}
    in_string = False
    escaped = False
    for index, character in enumerate(value):
        if in_string:
            if escaped:
                escaped = False
            elif character == "\\":
                escaped = True
            elif character == '"':
                in_string = False
            continue
        if character == '"':
            in_string = True
            continue
        if character in depths:
            depths[character] += 1
        elif character in pairs:
            depths[pairs[character]] -= 1
        elif character == "," and all(depth == 0 for depth in depths.values()):
            result.append(value[start:index].strip())
            start = index + 1
    tail = value[start:].strip()
    if tail:
        result.append(tail)
    return result


def parse_parameter(value: str) -> Parameter:
    value = re.sub(r"\[[^\]]+\]\s*", "", value).strip()
    default_value = None
    pieces = split_top_level(value.replace("=", ",=", 1)) if "=" in value else [value]
    if "=" in value:
        declaration, default_value = value.split("=", 1)
        declaration = declaration.strip()
        default_value = default_value.strip()
    else:
        declaration = value
    declaration = re.sub(r"\b(?:in|out|ref|scoped|params|this)\s+", "", declaration).strip()
    match = re.match(r"(?P<type>.+?)\s+(?P<name>@?\w+)$", declaration)
    if not match:
        fail(f"Cannot parse Analytics service parameter: {value}")
    return Parameter(match.group("type").strip(), match.group("name").lstrip("@"), default_value)


def extract_balanced(text: str, opening_index: int) -> tuple[str, int]:
    if text[opening_index] != "(":
        fail("Internal parser error: expected opening parenthesis.")
    depth = 0
    for index in range(opening_index, len(text)):
        character = text[index]
        if character == "(":
            depth += 1
        elif character == ")":
            depth -= 1
            if depth == 0:
                return text[opening_index + 1 : index], index + 1
    fail("Unbalanced Analytics method declaration.")


def parse_service(path: Path) -> list[Method]:
    text = path.read_text(encoding="utf-8")
    class_match = re.search(r"public\s+sealed\s+class\s+(\w+)", text)
    if not class_match:
        fail(f"Cannot find public service class in {path.relative_to(ROOT)}")
    service_name = class_match.group(1)
    pattern = re.compile(
        r"public\s+(?:async\s+)?(?P<return>(?:Task|ValueTask)(?:<[^\n{;]+>)?)\s+"
        r"(?P<name>\w+Async)\s*\(",
        re.MULTILINE,
    )
    methods: list[Method] = []
    for match in pattern.finditer(text):
        parameter_text, _ = extract_balanced(text, match.end() - 1)
        parameters = tuple(
            parse_parameter(value)
            for value in split_top_level(parameter_text)
            if value.strip()
        )
        methods.append(
            Method(
                service_name,
                match.group("name"),
                " ".join(match.group("return").split()),
                parameters,
            )
        )
    if not methods:
        fail(f"No public asynchronous application method found in {path.relative_to(ROOT)}")
    return methods


def all_source() -> str:
    return "\n".join(
        path.read_text(encoding="utf-8")
        for path in sorted(ANALYTICS.rglob("*.cs"))
        if "/bin/" not in path.as_posix() and "/obj/" not in path.as_posix()
    )


def find_factory(type_name: str, source: str) -> tuple[str, tuple[Parameter, ...]] | None:
    simple = type_name.rstrip("?").split(".")[-1]
    pattern = re.compile(
        rf"public\s+static\s+{re.escape(simple)}\s+(?P<name>Create|Start|From)\s*\(",
        re.MULTILINE,
    )
    match = pattern.search(source)
    if match:
        parameter_text, _ = extract_balanced(source, match.end() - 1)
        return match.group("name"), tuple(
            parse_parameter(value)
            for value in split_top_level(parameter_text)
            if value.strip()
        )
    record = re.search(rf"public\s+sealed\s+record\s+{re.escape(simple)}\s*\(", source)
    if record:
        parameter_text, _ = extract_balanced(source, record.end() - 1)
        return "new", tuple(
            parse_parameter(value)
            for value in split_top_level(parameter_text)
            if value.strip()
        )
    return None


def is_request(parameter: Parameter) -> bool:
    simple = parameter.type_name.rstrip("?").split(".")[-1]
    return simple.endswith("Request") or simple.endswith("Command")


def is_primitive(type_name: str) -> bool:
    simple = type_name.rstrip("?")
    return simple in {
        "string",
        "Guid",
        "DateOnly",
        "DateTimeOffset",
        "int",
        "long",
        "bool",
        "decimal",
    }


def actor_expression(type_name: str) -> str:
    simple = type_name.rstrip("?").split(".")[-1]
    return f"AnalyticsActorAccessor.Require{simple}(HttpContext)"


def environment_expression(parameter: Parameter) -> str | None:
    name = parameter.name.lower()
    simple = parameter.type_name.rstrip("?")
    if simple == "CancellationToken":
        return "cancellationToken"
    if "idempotency" in name:
        return "AnalyticsHttpCommand.RequireIdempotencyKey(Request)"
    if "correlation" in name:
        return "AnalyticsHttpCommand.RequireCorrelationId(HttpContext)"
    if "useragent" in name or name == "userAgent".lower():
        return "Request.Headers.UserAgent.ToString()"
    if "ipaddress" in name or name in {"remoteip", "clientip"}:
        if "IPAddress" in simple:
            return "HttpContext.Connection.RemoteIpAddress"
        return "HttpContext.Connection.RemoteIpAddress?.ToString()"
    if simple == "TimeProvider":
        return "TimeProvider.System"
    if simple == "DateTimeOffset" and name in {"nowutc", "receivedatutc"}:
        return "TimeProvider.System.GetUtcNow()"
    if "Actor" in simple or name in {"actor", "analyticsactor"}:
        return actor_expression(simple)
    return None


def factory_expression(type_name: str, source: str, stack: tuple[str, ...] = ()) -> str:
    simple = type_name.rstrip("?").split(".")[-1]
    if simple in stack:
        fail(f"Recursive Analytics context factory detected: {' -> '.join((*stack, simple))}")
    factory = find_factory(simple, source)
    if factory is None:
        fail(
            f"Analytics parameter type '{simple}' has no public Create/Start/From factory "
            "or public primary record constructor. Runtime reflection is forbidden."
        )
    factory_name, factory_parameters = factory
    arguments: list[str] = []
    for parameter in factory_parameters:
        expression = environment_expression(parameter)
        if expression is None:
            nested_simple = parameter.type_name.rstrip("?").split(".")[-1]
            if is_primitive(parameter.type_name):
                if parameter.default_value is not None:
                    expression = parameter.default_value
                elif nested_simple.endswith("?"):
                    expression = "null"
                else:
                    fail(
                        f"Cannot map factory parameter '{simple}.{parameter.name}' "
                        f"of type '{parameter.type_name}' to HTTP context."
                    )
            else:
                expression = factory_expression(parameter.type_name, source, (*stack, simple))
        arguments.append(expression)
    joined = ",\n                ".join(arguments)
    if factory_name == "new":
        return f"new {simple}(\n                {joined})"
    return f"{simple}.{factory_name}(\n                {joined})"


def controller_parameter(parameter: Parameter, route_names: set[str]) -> str:
    attribute = "[FromRoute] " if parameter.name in route_names else "[FromQuery] "
    default = f" = {parameter.default_value}" if parameter.default_value is not None else ""
    return f"{attribute}{parameter.type_name} {parameter.name}{default}"


def generate_call(method: Method, source: str, request_name: str | None) -> tuple[list[str], list[str]]:
    signature_parameters: list[str] = []
    arguments: list[str] = []
    route_names = {"listingId", "actorId", "campaignId", "placementId"}
    for parameter in method.parameters:
        if parameter.type_name.rstrip("?") == "CancellationToken":
            signature_parameters.append("CancellationToken cancellationToken")
            arguments.append("cancellationToken")
            continue
        if request_name is not None and parameter.name == request_name:
            arguments.append(request_name)
            continue
        expression = environment_expression(parameter)
        if expression is not None:
            arguments.append(expression)
            continue
        if is_primitive(parameter.type_name):
            signature_parameters.append(controller_parameter(parameter, route_names))
            arguments.append(parameter.name)
            continue
        arguments.append(factory_expression(parameter.type_name, source))
    return signature_parameters, arguments


def format_parameters(parameters: list[str], indent: int = 8) -> str:
    if not parameters:
        return ""
    separator = ",\n" + " " * indent
    return separator.join(parameters)


def generate_submit_controller(method: Method, source: str) -> str:
    request_parameters = [parameter for parameter in method.parameters if is_request(parameter)]
    if len(request_parameters) != 1:
        fail(
            f"{method.service_name}.{method.name} must expose exactly one request/command parameter; "
            f"found {len(request_parameters)}."
        )
    request = request_parameters[0]
    extra_signature, arguments = generate_call(method, source, request.name)
    signature = [f"[FromBody] {request.type_name} {request.name}", *extra_signature]
    argument_block = format_parameters(arguments, 12)
    return f'''using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Analytics.Api;

[ApiController]
[Route("api/analytics/events")]
[EnableRateLimiting(AnalyticsRateLimitPolicies.Events)]
public sealed class AnalyticsEventsController({method.service_name} service) : ControllerBase
{{
    [HttpPost(Name = AnalyticsOperationIds.SubmitInteractionEvent)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> SubmitAsync(
        {format_parameters(signature, 8)})
    {{
        ArgumentNullException.ThrowIfNull({request.name});
        var result = await service.{method.name}(
            {argument_block});
        return Accepted(result);
    }}
}}
'''


def generate_metrics_controller(method: Method, source: str) -> str:
    request_parameters = [parameter for parameter in method.parameters if is_request(parameter)]
    request_name = request_parameters[0].name if len(request_parameters) == 1 else None
    if len(request_parameters) > 1:
        fail(f"{method.service_name}.{method.name} exposes multiple request objects.")
    extra_signature, arguments = generate_call(method, source, request_name)
    signature: list[str] = []
    if request_name is not None:
        request = request_parameters[0]
        signature.append(f"[FromQuery] {request.type_name} {request.name}")
    signature.extend(extra_signature)
    argument_block = format_parameters(arguments, 12)
    route = "api/analytics/listings/{listingId:guid}/metrics/daily"
    return f'''using Aggregator.Analytics.Application;
using Aggregator.Analytics.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Aggregator.Analytics.Api;

[ApiController]
[Route("{route}")]
[EnableRateLimiting(AnalyticsRateLimitPolicies.Metrics)]
[Authorize(Policy = AnalyticsAuthorizationPolicies.ReadMetrics)]
public sealed class AnalyticsMetricsController({method.service_name} service) : ControllerBase
{{
    [HttpGet(Name = AnalyticsOperationIds.ReadDailyListingMetrics)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReadAsync(
        {format_parameters(signature, 8)})
    {{
        var result = await service.{method.name}(
            {argument_block});
        return Ok(result);
    }}
}}
'''


def generate_api(submit: Method, metrics: Method, source: str) -> None:
    write(
        API / "Analytics.Api.csproj",
        '''<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Microsoft.OpenApi" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="../Analytics.Application/Analytics.Application.csproj" />
    <ProjectReference Include="../Analytics.Contracts/Analytics.Contracts.csproj" />
    <ProjectReference Include="../Analytics.Infrastructure/Analytics.Infrastructure.csproj" />
    <ProjectReference Include="../../BuildingBlocks/Platform.Observability/Platform.Observability.csproj" />
    <ProjectReference Include="../../BuildingBlocks/Platform.ProblemDetails/Platform.ProblemDetails.csproj" />
    <ProjectReference Include="../../BuildingBlocks/Platform.Security/Platform.Security.csproj" />
  </ItemGroup>
</Project>''',
    )
    write(API / "AnalyticsEventsController.cs", generate_submit_controller(submit, source))
    write(API / "AnalyticsMetricsController.cs", generate_metrics_controller(metrics, source))
    write(
        API / "AnalyticsApiContracts.cs",
        '''namespace Aggregator.Analytics.Api;

public static class AnalyticsAuthorizationPolicies
{
    public const string ReadMetrics = "analytics.metrics.read";
    public const string Admin = "analytics.admin";
    public const string TestContracts = "analytics.test-contracts";
}

internal static class AnalyticsRateLimitPolicies
{
    public const string Events = "analytics-events";
    public const string Metrics = "analytics-metrics";
}

public static class AnalyticsOperationIds
{
    public const string SubmitInteractionEvent = "SubmitAnalyticsInteractionEvent";
    public const string ReadDailyListingMetrics = "ReadDailyListingMetrics";
}''',
    )
    write(
        API / "AnalyticsHttpCommand.cs",
        '''using Platform.ProblemDetails;

namespace Aggregator.Analytics.Api;

internal static class AnalyticsHttpCommand
{
    public static string RequireIdempotencyKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = request.Headers["Idempotency-Key"];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) ||
            values[0]!.Length > 200 || values[0]!.Any(char.IsControl))
        {
            throw new OwnerException(new OwnerError(
                "Analytics.Commands",
                "ANALYTICS_IDEMPOTENCY_KEY_REQUIRED",
                "Analytics Idempotency-Key is required",
                StatusCodes.Status400BadRequest,
                "The event command requires exactly one printable Idempotency-Key of at most 200 characters.",
                "Submit one stable key for this exact client event."));
        }

        return values[0]!.Trim();
    }

    public static string RequireCorrelationId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
        return correlation.CorrelationId ?? Guid.CreateVersion7().ToString("D");
    }
}''',
    )
    actor_types = sorted(
        {
            parameter.type_name.rstrip("?").split(".")[-1]
            for method in (submit, metrics)
            for parameter in method.parameters
            if "Actor" in parameter.type_name
        }
    )
    actor_methods: list[str] = []
    for actor_type in actor_types:
        actor_methods.append(
            f'''    public static {actor_type} Require{actor_type}(HttpContext context)
    {{
        ArgumentNullException.ThrowIfNull(context);
        var value = context.User.FindFirst("actor_id")?.Value;
        if (Guid.TryParse(value, out var actorId) && actorId != Guid.Empty)
        {{
            return {actor_type}.Create(actorId);
        }}

        throw new OwnerException(new OwnerError(
            "Analytics.Access",
            "ANALYTICS_ACTOR_MAPPING_REQUIRED",
            "Analytics actor mapping is required",
            StatusCodes.Status403Forbidden,
            "The authenticated subject has no valid internal Analytics actor mapping.",
            "Register the subject and issue an actor_id projection before retrying."));
    }}'''
        )
    write(
        API / "AnalyticsActorAccessor.cs",
        '''using Aggregator.Analytics.Application;
using Platform.ProblemDetails;

namespace Aggregator.Analytics.Api;

internal static class AnalyticsActorAccessor
{
''' + "\n\n".join(actor_methods) + "\n}\n",
    )
    write(
        API / "AnalyticsFailureMiddleware.cs",
        '''using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Platform.ProblemDetails;

namespace Aggregator.Analytics.Api;

internal sealed class AnalyticsFailureMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context);
        }
        catch (OwnerException)
        {
            throw;
        }
        catch (AnalyticsCommandException exception)
        {
            throw new OwnerException(new OwnerError(
                exception.Owner,
                exception.Code,
                "Analytics command rejected",
                exception.StatusCode,
                exception.Message,
                exception.RequiredAction,
                exception.Context), exception);
        }
        catch (AnalyticsDomainException exception)
        {
            throw new OwnerException(new OwnerError(
                "Analytics.Domain",
                exception.Code,
                "Analytics domain invariant rejected",
                StatusCodes.Status422UnprocessableEntity,
                exception.Message,
                "Correct the event or aggregate request before retrying."), exception);
        }
    }
}''',
    )
    write(
        API / "Program.cs",
        '''using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;
using Platform.ProblemDetails;
using Platform.Security;

namespace Aggregator.Analytics.Api;

public partial class Program
{
    public static void Main(string[] args) => CreateApplication(args).Run();

    public static WebApplication CreateApplication(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 512 * 1024);
        builder.Services.AddControllers().AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            options.JsonSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        });
        builder.Services.Configure<ApiBehaviorOptions>(options =>
            options.InvalidModelStateResponseFactory = context => throw new OwnerException(new OwnerError(
                "Analytics.Transport",
                "ANALYTICS_REQUEST_CONTRACT_INVALID",
                "Analytics request contract is invalid",
                StatusCodes.Status400BadRequest,
                "The request cannot be bound to the active Analytics contract.",
                "Correct the reported JSON fields and use only declared string enum tokens.")));
        builder.Services.AddOpenApi("analytics");
        builder.Services.AddOwnerProblemDetails();
        builder.Services.AddAnalyticsApplication();
        builder.Services.AddAnalyticsInfrastructure(builder.Configuration);
        builder.Services.AddPlatformObservability(builder.Configuration, "analytics-api");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(AnalyticsRateLimitPolicies.Events, limiter =>
            {
                limiter.PermitLimit = 240;
                limiter.QueueLimit = 0;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.AutoReplenishment = true;
            });
            options.AddFixedWindowLimiter(AnalyticsRateLimitPolicies.Metrics, limiter =>
            {
                limiter.PermitLimit = 60;
                limiter.QueueLimit = 0;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.AutoReplenishment = true;
            });
        });
        var authorization = builder.Services.AddPlatformJwtAuthentication(
            builder.Configuration,
            audience: "aggregator-analytics");
        authorization
            .AddRequiredScopePolicy(AnalyticsAuthorizationPolicies.ReadMetrics, AnalyticsAuthorizationPolicies.ReadMetrics)
            .AddRequiredScopePolicy(AnalyticsAuthorizationPolicies.Admin, AnalyticsAuthorizationPolicies.Admin)
            .AddRequiredScopePolicy(AnalyticsAuthorizationPolicies.TestContracts, AnalyticsAuthorizationPolicies.TestContracts);

        var application = builder.Build();
        application.UseOwnerProblemDetails();
        application.UseMiddleware<AnalyticsFailureMiddleware>();
        application.UseRateLimiter();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/health/live", () => Results.Ok(new
        {
            owner = "Analytics.Runtime",
            state = "live",
        })).AllowAnonymous();
        application.MapGet("/health/ready", async (
            AnalyticsReadinessProbe readiness,
            CancellationToken cancellationToken) =>
        {
            var ready = await readiness.CanConnectAsync(cancellationToken);
            return ready
                ? Results.Ok(new { owner = "Analytics.Persistence", state = "ready" })
                : Results.Problem(
                    title: "Analytics database unavailable",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    detail: "The Analytics API cannot reach analytics_db.");
        }).AllowAnonymous();
        application.MapControllers();
        if (application.Environment.IsDevelopment())
        {
            application.MapOpenApi("/openapi/{documentName}.json")
                .RequireAuthorization(AnalyticsAuthorizationPolicies.TestContracts);
        }

        return application;
    }
}''',
    )


def emit_schema() -> None:
    with tempfile.TemporaryDirectory(prefix="analytics-schema-") as temporary:
        temp = Path(temporary)
        project_reference = (INFRASTRUCTURE / "Analytics.Infrastructure.csproj").relative_to(temp, walk_up=True)
        write(
            temp / "SchemaEmitter.csproj",
            f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><ProjectReference Include="{project_reference.as_posix()}" /></ItemGroup>
</Project>''',
        )
        write(
            temp / "Program.cs",
            '''using Aggregator.Analytics.Infrastructure;
using Microsoft.EntityFrameworkCore;

var options = new DbContextOptionsBuilder<AnalyticsDbContext>()
    .UseNpgsql("Host=localhost;Database=analytics_db;Username=analytics_migrator;Password=unused")
    .Options;
await using var context = new AnalyticsDbContext(options);
Console.Write(context.Database.GenerateCreateScript());''',
        )
        completed = subprocess.run(
            ["dotnet", "run", "--project", str(temp / "SchemaEmitter.csproj"), "--configuration", "Release"],
            cwd=ROOT,
            check=False,
            capture_output=True,
            text=True,
        )
        if completed.returncode != 0:
            fail("Analytics schema generation failed:\n" + completed.stdout + "\n" + completed.stderr)
        schema = completed.stdout.strip()
        if "CREATE TABLE" not in schema.upper():
            fail("Analytics EF model generated no CREATE TABLE statements.")
        write(MIGRATIONS / "Migrations" / "V001__analytics_owner_schema.sql", schema)


def generate_migrations() -> None:
    catalog_project = ROOT / "src" / "Catalog" / "Catalog.Migrations" / "Catalog.Migrations.csproj"
    catalog_program = ROOT / "src" / "Catalog" / "Catalog.Migrations" / "Program.cs"
    if not catalog_project.exists() or not catalog_program.exists():
        fail("Catalog migration template is unavailable.")
    project = catalog_project.read_text(encoding="utf-8")
    project = project.replace("Catalog.Migrations", "Analytics.Migrations")
    write(MIGRATIONS / "Analytics.Migrations.csproj", project)
    program = catalog_program.read_text(encoding="utf-8")
    program = program.replace("Catalog", "Analytics").replace("CATALOG", "ANALYTICS")
    write(MIGRATIONS / "Program.cs", program)
    emit_schema()


def main() -> int:
    submit_methods = parse_service(APPLICATION / "SubmitInteractionEventService.cs")
    metric_methods = parse_service(APPLICATION / "ReadDailyListingMetricsService.cs")
    if len(submit_methods) != 1:
        fail(f"Expected one public submit method, found {len(submit_methods)}.")
    if len(metric_methods) != 1:
        fail(f"Expected one public metric read method, found {len(metric_methods)}.")
    source = all_source()
    generate_api(submit_methods[0], metric_methods[0], source)
    generate_migrations()
    REPORT.parent.mkdir(parents=True, exist_ok=True)
    REPORT.write_text(
        "# Analytics runtime generation\n\n"
        f"- Submit owner: `{submit_methods[0].service_name}.{submit_methods[0].name}`.\n"
        f"- Metrics owner: `{metric_methods[0].service_name}.{metric_methods[0].name}`.\n"
        "- API transport is strongly typed and contains no runtime reflection.\n"
        "- Migration SQL was generated from the committed EF design model and is committed for checksum verification.\n",
        encoding="utf-8",
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exception:  # noqa: BLE001 - fail-closed CLI boundary
        print(str(exception), file=sys.stderr)
        raise
