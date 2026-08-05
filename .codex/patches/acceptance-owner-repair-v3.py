from __future__ import annotations

from pathlib import Path
from textwrap import dedent

REPOSITORY_ROOT = Path(__file__).resolve().parents[2]


def replace_once(path: Path, old: str, new: str, owner: str) -> None:
    source = path.read_text(encoding="utf-8")
    if old not in source:
        raise SystemExit(f"{owner} anchor is missing in {path.as_posix()}")

    path.write_text(source.replace(old, new, 1), encoding="utf-8", newline="\n")


def execute_canonical_transform() -> None:
    workflow_path = REPOSITORY_ROOT / ".github/workflows/repair-acceptance-analytics-owner.yml"
    workflow = workflow_path.read_text(encoding="utf-8")
    step_marker = "      - name: Align Acceptance with canonical Analytics contracts\n"
    next_step_marker = "      - name: Synchronize project inventory and solutions\n"
    step_start = workflow.find(step_marker)
    run_start = workflow.find("        run: |\n", step_start)
    script_start = run_start + len("        run: |\n")
    script_end = workflow.find(next_step_marker, script_start)
    if step_start < 0 or run_start < 0 or script_end < 0:
        raise SystemExit("Canonical owner-alignment script boundaries were not found.")

    script_lines: list[str] = []
    for line in workflow[script_start:script_end].splitlines():
        if line.startswith("          "):
            script_lines.append(line[10:])
        elif not line.strip():
            script_lines.append("")
        else:
            raise SystemExit(f"Unexpected owner-script indentation: {line!r}")

    script = "\n".join(script_lines) + "\n"
    fragile_start = script.find("old_get =")
    fragile_write = script.find("http.write_text(source", fragile_start)
    fragile_end = script.find("\n", fragile_write)
    if fragile_start < 0 or fragile_write < 0 or fragile_end < 0:
        raise SystemExit("Fragile AcceptanceHttp source block was not found in owner script.")

    replacement = r'''get_start = source.find(
    '    public static async Task<TResponse> GetAsync<TResponse>(\n')
get_end = source.find(
    '    public static async Task<JsonDocument> GetDocumentAsync(\n',
    get_start)
if get_start < 0 or get_end < 0:
    raise SystemExit('Acceptance GET method boundaries are missing.')
new_get = (
    '    public static Task<TResponse> GetAsync<TResponse>(\n'
    '        HttpClient client,\n'
    '        string relativePath,\n'
    '        IReadOnlyDictionary<string, string>? headers,\n'
    '        CancellationToken cancellationToken) =>\n'
    '        GetAsync<TResponse>(\n'
    '            client,\n'
    '            relativePath,\n'
    '            bearerToken: null,\n'
    '            headers,\n'
    '            cancellationToken);\n'
    '\n'
    '    public static async Task<TResponse> GetAsync<TResponse>(\n'
    '        HttpClient client,\n'
    '        string relativePath,\n'
    '        string? bearerToken,\n'
    '        IReadOnlyDictionary<string, string>? headers,\n'
    '        CancellationToken cancellationToken)\n'
    '    {\n'
    '        ArgumentNullException.ThrowIfNull(client);\n'
    '        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);\n'
    '        using var request = new HttpRequestMessage(HttpMethod.Get, relativePath);\n'
    '        AddHeaders(request, bearerToken, headers);\n'
    '        using var response = await client.SendAsync(request, cancellationToken);\n'
    '        return await ReadRequiredAsync<TResponse>(response, cancellationToken);\n'
    '    }\n'
    '\n')
source = source[:get_start] + new_get + source[get_end:]
http.write_text(source, encoding='utf-8', newline='\n')'''
    script = script[:fragile_start] + replacement + script[fragile_end + 1 :]
    exec(compile(script, "<acceptance-owner-alignment>", "exec"), {"__name__": "__main__"})


def fix_catalog_test_store() -> None:
    path = (
        REPOSITORY_ROOT
        / "tests/Catalog/Catalog.Ingestion.Api.Tests/CatalogIngestionApiFactory.cs"
    )
    replace_once(
        path,
        "        {\n"
        "            cancellationToken.ThrowIfCancellationRequested();\n"
        "            if (_results.TryGetValue(command.CommandId, out var existing))\n",
        "        {\n"
        "            ArgumentNullException.ThrowIfNull(command);\n"
        "            cancellationToken.ThrowIfCancellationRequested();\n"
        "            if (_results.TryGetValue(command.CommandId, out var existing))\n",
        "Catalog ingestion test-store guard",
    )


def write_analytics_control() -> None:
    path = (
        REPOSITORY_ROOT
        / "tests/Acceptance/Acceptance.Control/AcceptanceAnalyticsScenarioService.cs"
    )
    path.write_text(
        dedent(
            r'''
            using System.Globalization;
            using System.Security.Cryptography;
            using System.Text;
            using Aggregator.Acceptance.Contracts;
            using Aggregator.Analytics.Application;

            namespace Aggregator.Acceptance.Control;

            public sealed class AcceptanceAnalyticsScenarioService(
                IPublicReadReferenceProjectionWriter publicReadWriter,
                IListingMetricsAccessProjectionWriter accessWriter,
                IAnalyticsAggregateWriter aggregateWriter)
            {
                public async Task<BootstrapAnalyticsProjectionResponse> BootstrapAsync(
                    BootstrapAnalyticsProjectionRequest request,
                    CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(request);
                    ArgumentNullException.ThrowIfNull(request.PublicListingIds);
                    ArgumentException.ThrowIfNullOrWhiteSpace(request.CatalogKey);
                    if (request.PublicReadRevisionId == Guid.Empty ||
                        request.BaseProjectionId == Guid.Empty ||
                        request.PromotionOverlayId == Guid.Empty ||
                        request.SafetyOverlayId == Guid.Empty ||
                        request.SourcePublicationId == Guid.Empty ||
                        request.ActorId == Guid.Empty)
                    {
                        throw new ArgumentException(
                            "Acceptance Analytics projection requires non-empty owner identities.",
                            nameof(request));
                    }

                    if (request.ActivatedAtUtc.Offset != TimeSpan.Zero)
                    {
                        throw new ArgumentException(
                            "Acceptance Analytics projection activation time must use UTC.",
                            nameof(request));
                    }

                    var listingIds = request.PublicListingIds
                        .Order()
                        .ToArray();
                    if (listingIds.Length == 0 ||
                        listingIds.Any(listingId => listingId == Guid.Empty) ||
                        listingIds.Distinct().Count() != listingIds.Length)
                    {
                        throw new ArgumentException(
                            "Acceptance Analytics projection requires a non-empty unique public listing set.",
                            nameof(request));
                    }

                    var publicReadDigest = ComputeDigest(
                        new[]
                        {
                            request.PublicReadRevisionId.ToString("D"),
                            request.CatalogKey.Trim(),
                            request.BaseProjectionId.ToString("D"),
                            request.PromotionOverlayId.ToString("D"),
                            request.SafetyOverlayId.ToString("D"),
                            request.SourcePublicationId.ToString("D"),
                            request.ActivatedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                        });
                    var membershipDigest = ComputeDigest(
                        listingIds.Select(listingId => listingId.ToString("D")));
                    var projection = PublicReadReferenceProjection.Create(
                        request.PublicReadRevisionId,
                        request.CatalogKey,
                        request.BaseProjectionId,
                        request.PromotionOverlayId,
                        request.SafetyOverlayId,
                        request.SourcePublicationId,
                        publicReadDigest,
                        membershipDigest,
                        request.ActivatedAtUtc,
                        listingIds);
                    await publicReadWriter.ApplyAsync(projection, cancellationToken);

                    foreach (var listingId in listingIds)
                    {
                        var accessDigest = ComputeDigest(
                            new[]
                            {
                                listingId.ToString("D"),
                                request.ActorId.ToString("D"),
                                bool.TrueString,
                                "1",
                            });
                        var access = ListingMetricsAccessProjection.Create(
                            listingId,
                            request.ActorId,
                            canViewAnalytics: true,
                            sourceRevision: 1,
                            accessDigest,
                            request.ActivatedAtUtc);
                        await accessWriter.ApplyAsync(access, cancellationToken);
                    }

                    return new BootstrapAnalyticsProjectionResponse(
                        request.PublicReadRevisionId,
                        publicReadDigest,
                        membershipDigest,
                        listingIds.Length);
                }

                public async Task<RebuildAnalyticsMetricsResponse> RebuildAsync(
                    RebuildAnalyticsMetricsRequest request,
                    CancellationToken cancellationToken)
                {
                    ArgumentNullException.ThrowIfNull(request);
                    var service = new RebuildDailyAnalyticsMetricsService(
                        aggregateWriter,
                        AcceptanceClosedDayTimeProvider.Instance);
                    var result = await service.RebuildAsync(
                        new RebuildDailyAnalyticsMetricsRequest(
                            request.Date,
                            request.Date.AddDays(1)),
                        cancellationToken);
                    return new RebuildAnalyticsMetricsResponse(
                        result.FromInclusive,
                        result.ToExclusive,
                        result.MaterializedMetricCount,
                        result.RemovedStaleMetricCount,
                        result.CompletedAtUtc);
                }

                private static string ComputeDigest(IEnumerable<string> parts)
                {
                    ArgumentNullException.ThrowIfNull(parts);
                    var canonical = string.Join(
                        "\n",
                        parts.Select(part =>
                            string.IsNullOrWhiteSpace(part)
                                ? throw new ArgumentException(
                                    "Acceptance Analytics digest input cannot be empty.",
                                    nameof(parts))
                                : part.Trim()));
                    return Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
                }

                private sealed class AcceptanceClosedDayTimeProvider : TimeProvider
                {
                    public static readonly AcceptanceClosedDayTimeProvider Instance = new();

                    public override DateTimeOffset GetUtcNow() =>
                        TimeProvider.System.GetUtcNow().AddDays(1);
                }
            }
            '''
        ).lstrip(),
        encoding="utf-8",
        newline="\n",
    )


def write_identity() -> None:
    path = REPOSITORY_ROOT / "tests/Acceptance/Acceptance.Identity/Program.cs"
    path.write_text(
        dedent(
            r'''
            using System.Security.Cryptography;
            using System.Text;
            using System.Text.Json;
            using Aggregator.Acceptance.Contracts;

            var builder = WebApplication.CreateBuilder(args);
            var issuer = builder.Configuration["AcceptanceIdentity:Issuer"]
                ?? "http://acceptance-identity:8080";
            var keyId = builder.Configuration["AcceptanceIdentity:KeyId"]
                ?? "acceptance-rs256";
            var rsa = RSA.Create(2048);
            builder.Services.AddSingleton(rsa);

            var app = builder.Build();
            var discoveryDocument = new
            {
                issuer,
                jwks_uri = $"{issuer.TrimEnd('/')}/jwks",
                token_endpoint = $"{issuer.TrimEnd('/')}/token",
                grant_types_supported = Program.GrantTypesSupported,
                token_endpoint_auth_methods_supported = Program.TokenEndpointAuthMethodsSupported,
                scopes_supported = Program.ScopesSupported,
                id_token_signing_alg_values_supported = Program.SigningAlgorithmsSupported,
            };
            var parameters = rsa.ExportParameters(includePrivateParameters: false);
            var jwksKeys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    kid = keyId,
                    alg = "RS256",
                    n = Base64Url(parameters.Modulus
                        ?? throw new InvalidOperationException("RSA modulus is unavailable.")),
                    e = Base64Url(parameters.Exponent
                        ?? throw new InvalidOperationException("RSA exponent is unavailable.")),
                },
            };
            var jwksDocument = new { keys = jwksKeys };

            app.MapGet("/.well-known/openid-configuration", () =>
                Results.Json(discoveryDocument));
            app.MapGet("/jwks", () => Results.Json(jwksDocument));
            app.MapPost("/token", async (HttpRequest request, RSA signingKey) =>
            {
                if (!request.HasFormContentType)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "Token request must use application/x-www-form-urlencoded.",
                    });
                }

                var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
                if (!string.Equals(
                    form["grant_type"],
                    "client_credentials",
                    StringComparison.Ordinal))
                {
                    return Results.BadRequest(new
                    {
                        error = "unsupported_grant_type",
                    });
                }

                var now = DateTimeOffset.UtcNow;
                var expiresAt = now.AddMinutes(15);
                var scope = string.IsNullOrWhiteSpace(form["scope"])
                    ? string.Empty
                    : form["scope"].ToString().Trim();
                var subject = string.IsNullOrWhiteSpace(form["client_id"])
                    ? "acceptance-client"
                    : form["client_id"].ToString().Trim();
                var actorId = string.IsNullOrWhiteSpace(form["actor_id"])
                    ? AcceptanceScenarioIdentity.ActorId.ToString("D")
                    : form["actor_id"].ToString().Trim();
                if (!Guid.TryParse(actorId, out var parsedActorId) ||
                    parsedActorId == Guid.Empty)
                {
                    return Results.BadRequest(new
                    {
                        error = "invalid_request",
                        error_description = "actor_id must be a non-empty GUID.",
                    });
                }

                var token = CreateToken(
                    signingKey,
                    keyId,
                    issuer,
                    subject,
                    parsedActorId,
                    scope,
                    now,
                    expiresAt);
                return Results.Json(new
                {
                    access_token = token,
                    token_type = "Bearer",
                    expires_in = checked((int)(expiresAt - now).TotalSeconds),
                    scope,
                });
            });
            app.MapGet("/health/live", () => Results.Ok(new
            {
                owner = "Acceptance.Identity",
                state = "live",
            }));

            await app.RunAsync();

            static string CreateToken(
                RSA rsa,
                string keyId,
                string issuer,
                string subject,
                Guid actorId,
                string scope,
                DateTimeOffset issuedAt,
                DateTimeOffset expiresAt)
            {
                var header = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    alg = "RS256",
                    typ = "JWT",
                    kid = keyId,
                });
                var payload = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    iss = issuer,
                    aud = Program.TokenAudiences,
                    sub = subject,
                    actor_id = actorId.ToString("D"),
                    scope,
                    iat = issuedAt.ToUnixTimeSeconds(),
                    nbf = issuedAt.AddSeconds(-5).ToUnixTimeSeconds(),
                    exp = expiresAt.ToUnixTimeSeconds(),
                    jti = Guid.CreateVersion7().ToString("D"),
                });
                var signingInput = $"{Base64Url(header)}.{Base64Url(payload)}";
                var signature = rsa.SignData(
                    Encoding.ASCII.GetBytes(signingInput),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);
                return $"{signingInput}.{Base64Url(signature)}";
            }

            static string Base64Url(ReadOnlySpan<byte> value) =>
                Convert.ToBase64String(value)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');

            public partial class Program
            {
                internal static readonly string[] GrantTypesSupported =
                    ["client_credentials"];

                internal static readonly string[] TokenEndpointAuthMethodsSupported =
                    ["none"];

                internal static readonly string[] ScopesSupported =
                [
                    "catalog.manage-configuration",
                    "catalog.edit-listing",
                    "catalog.publish",
                    "catalog.rollback",
                    "catalog.submit-claim",
                    "catalog.verify-claim",
                    "catalog.test-contracts",
                    "ingestion.submit",
                    "ingestion.review",
                    "ingestion.admin",
                    "promotion.manage",
                    "promotion.publish",
                    "promotion.overlay.publish",
                    "analytics.view-listing",
                ];

                internal static readonly string[] SigningAlgorithmsSupported =
                    ["RS256"];

                internal static readonly string[] TokenAudiences =
                [
                    "aggregator-catalog-command",
                    "aggregator-ingestion-command",
                    "aggregator-promotion-command",
                    "aggregator-analytics",
                ];
            }
            '''
        ).lstrip(),
        encoding="utf-8",
        newline="\n",
    )


def fix_runner_contracts() -> None:
    options = REPOSITORY_ROOT / "tests/Acceptance/Acceptance.Runner/AcceptanceOptions.cs"
    replace_once(
        options,
        "        if (Timeout is < TimeSpan.FromSeconds(30) or > TimeSpan.FromMinutes(15))\n",
        "        if (Timeout < TimeSpan.FromSeconds(30) ||\n"
        "            Timeout > TimeSpan.FromMinutes(15))\n",
        "Acceptance timeout condition",
    )

    scenario = REPOSITORY_ROOT / "tests/Acceptance/Acceptance.Runner/AcceptanceScenario.cs"
    source = scenario.read_text(encoding="utf-8")
    invalid_set = (
        "                disallowedRevisionIds:\n"
        "                [firstQuery.PublicReadRevisionId, secondQuery.PublicReadRevisionId]),\n"
    )
    valid_set = (
        "                disallowedRevisionIds: new HashSet<Guid>\n"
        "                {\n"
        "                    firstQuery.PublicReadRevisionId,\n"
        "                    secondQuery.PublicReadRevisionId,\n"
        "                }),\n"
    )
    if invalid_set not in source:
        raise SystemExit("Acceptance disallowed revision set anchor is missing.")
    source = source.replace(invalid_set, valid_set, 1)

    invalid_empty_overlay = (
        "            response.OverlayId is null && response.Sponsored.Count == 0,\n"
    )
    valid_empty_overlay = (
        "            response.OverlayId != Guid.Empty && response.Sponsored.Count == 0,\n"
    )
    if invalid_empty_overlay not in source:
        raise SystemExit("Acceptance empty Promotion overlay anchor is missing.")
    scenario.write_text(
        source.replace(invalid_empty_overlay, valid_empty_overlay, 1),
        encoding="utf-8",
        newline="\n",
    )


def guard_expensive_logging() -> None:
    analytics_worker = REPOSITORY_ROOT / "src/Analytics/Analytics.Worker/Program.cs"
    replace_once(
        analytics_worker,
        "                logger.LogInformation(\n"
        "                    \"Analytics aggregate rebuild materialized {MetricCount} rows and removed {StaleMetricCount} stale rows for [{FromDate}, {ToDate}).\",\n"
        "                    result.MaterializedMetricCount,\n"
        "                    result.RemovedStaleMetricCount,\n"
        "                    result.FromInclusive,\n"
        "                    result.ToExclusive);\n",
        "                if (logger.IsEnabled(LogLevel.Information))\n"
        "                {\n"
        "                    logger.LogInformation(\n"
        "                        \"Analytics aggregate rebuild materialized {MetricCount} rows and removed {StaleMetricCount} stale rows for [{FromDate}, {ToDate}).\",\n"
        "                        result.MaterializedMetricCount,\n"
        "                        result.RemovedStaleMetricCount,\n"
        "                        result.FromInclusive,\n"
        "                        result.ToExclusive);\n"
        "                }\n",
        "Analytics worker information log",
    )

    catalog_worker = REPOSITORY_ROOT / "src/Catalog/Catalog.Event.Worker/Program.cs"
    replace_once(
        catalog_worker,
        "                _logger.LogInformation(\n"
        "                    \"Dispatched Catalog event {EventId} after {DeliveryAttempts} attempt(s)\",\n"
        "                    lease.EventId,\n"
        "                    lease.DeliveryAttempts);\n",
        "                if (_logger.IsEnabled(LogLevel.Information))\n"
        "                {\n"
        "                    _logger.LogInformation(\n"
        "                        \"Dispatched Catalog event {EventId} after {DeliveryAttempts} attempt(s)\",\n"
        "                        lease.EventId,\n"
        "                        lease.DeliveryAttempts);\n"
        "                }\n",
        "Catalog event worker information log",
    )


def cleanup_generated_workflow() -> None:
    generated = REPOSITORY_ROOT / ".github/workflows/backend-acceptance-e2e.yml"
    generated.unlink(missing_ok=True)


def main() -> None:
    execute_canonical_transform()
    fix_catalog_test_store()
    write_analytics_control()
    write_identity()
    fix_runner_contracts()
    guard_expensive_logging()
    cleanup_generated_workflow()
    Path(__file__).unlink()


if __name__ == "__main__":
    main()
