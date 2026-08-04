internal static class ApiTemplateWriter
{
    public static void Write(CatalogMediaGenerationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Directory.CreateDirectory(context.ApiDirectory);
        Write("Catalog.Media.Api.csproj", Project());
        Write("CatalogMediaApiContracts.cs", ApiContracts());
        Write("CatalogMediaHttpContext.cs", HttpContextSource());
        Write("CatalogMediaFailureMiddleware.cs", FailureMiddleware());
        Write("CatalogMediaController.cs", Controller());
        Write("Program.cs", ProgramSource());

        void Write(string name, string content) =>
            File.WriteAllText(Path.Combine(context.ApiDirectory, name), content.Trim() + Environment.NewLine);
    }

    private static string Project() =>
        """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <ItemGroup>
            <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
            <PackageReference Include="Microsoft.OpenApi" />
          </ItemGroup>
          <ItemGroup>
            <ProjectReference Include="../Catalog.Media.Application/Catalog.Media.Application.csproj" />
            <ProjectReference Include="../Catalog.Media.Contracts/Catalog.Media.Contracts.csproj" />
            <ProjectReference Include="../Catalog.Media.Infrastructure/Catalog.Media.Infrastructure.csproj" />
            <ProjectReference Include="../../BuildingBlocks/Platform.ObjectStorage/Platform.ObjectStorage.csproj" />
            <ProjectReference Include="../../BuildingBlocks/Platform.Observability/Platform.Observability.csproj" />
            <ProjectReference Include="../../BuildingBlocks/Platform.ProblemDetails/Platform.ProblemDetails.csproj" />
            <ProjectReference Include="../../BuildingBlocks/Platform.Security/Platform.Security.csproj" />
          </ItemGroup>
        </Project>
        """;

    private static string ApiContracts() =>
        """
        namespace Aggregator.CatalogMedia.Api;

        public static class CatalogMediaAuthorizationPolicies
        {
            public const string Manage = "catalog.media.manage";
            public const string Read = "catalog.media.read";
            public const string RevokeRights = "catalog.media.revoke-rights";
            public const string TestContracts = "catalog.media.test-contracts";
        }

        internal static class CatalogMediaRateLimitPolicies
        {
            public const string Commands = "catalog-media-commands";
            public const string Reads = "catalog-media-reads";
        }

        public static class CatalogMediaOperationIds
        {
            public const string Register = "RegisterCatalogMedia";
            public const string PrepareUpload = "PrepareCatalogMediaUpload";
            public const string CompleteUpload = "CompleteCatalogMediaUpload";
            public const string RevokeRights = "RevokeCatalogMediaRights";
            public const string Get = "GetCatalogMedia";
        }
        """;

    private static string HttpContextSource() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Platform.ProblemDetails;

        namespace Aggregator.CatalogMedia.Api;

        internal static class CatalogMediaHttpContext
        {
            public static string RequireIdempotencyKey(HttpRequest request)
            {
                ArgumentNullException.ThrowIfNull(request);
                var values = request.Headers["Idempotency-Key"];
                if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]) ||
                    values[0]!.Length > 200 || values[0]!.Any(char.IsControl))
                {
                    throw new OwnerException(new OwnerError(
                        "CatalogMedia.Commands",
                        "CATALOG_MEDIA_IDEMPOTENCY_KEY_REQUIRED",
                        "Catalog media Idempotency-Key is required",
                        StatusCodes.Status400BadRequest,
                        "A mutating media request requires exactly one printable Idempotency-Key of at most 200 characters.",
                        "Submit one stable key for this exact semantic command."));
                }
                return values[0]!.Trim();
            }

            public static CatalogMediaCommandContext CreateCommandContext(HttpContext context)
            {
                ArgumentNullException.ThrowIfNull(context);
                var actorValue = context.User.FindFirst("actor_id")?.Value;
                if (!Guid.TryParse(actorValue, out var actorId) || actorId == Guid.Empty)
                {
                    throw new OwnerException(new OwnerError(
                        "CatalogMedia.Access",
                        "CATALOG_MEDIA_ACTOR_MAPPING_REQUIRED",
                        "Catalog media actor mapping is required",
                        StatusCodes.Status403Forbidden,
                        "The authenticated identity has no valid internal media actor mapping.",
                        "Register the identity and issue an actor_id projection before retrying."));
                }
                var correlation = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
                return CatalogMediaCommandContext.Start(
                    CatalogMediaActor.Create(actorId),
                    correlation.CorrelationId);
            }
        }
        """;

    private static string FailureMiddleware() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Domain;
        using Platform.ProblemDetails;

        namespace Aggregator.CatalogMedia.Api;

        internal sealed class CatalogMediaFailureMiddleware(RequestDelegate next)
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
                catch (CatalogMediaApplicationException exception)
                {
                    throw new OwnerException(
                        new OwnerError(
                            exception.Owner,
                            exception.Code,
                            "Catalog media owner rejected the request",
                            exception.StatusCode,
                            exception.Message,
                            exception.RequiredAction,
                            exception.Context),
                        exception);
                }
                catch (CatalogMediaDomainException exception)
                {
                    throw new OwnerException(
                        new OwnerError(
                            "CatalogMedia.Domain",
                            exception.Code,
                            "Catalog media transition rejected",
                            StatusCodes.Status422UnprocessableEntity,
                            exception.Message,
                            "Correct the command input or expected aggregate revision before retrying."),
                        exception);
                }
            }
        }
        """;

    private static string Controller() =>
        """
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Contracts;
        using Microsoft.AspNetCore.Authorization;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.RateLimiting;

        namespace Aggregator.CatalogMedia.Api;

        [ApiController]
        [Route("api/catalog-media/assets")]
        public sealed class CatalogMediaController(CatalogMediaCommandService service) : ControllerBase
        {
            [HttpPost(Name = CatalogMediaOperationIds.Register)]
            [Authorize(Policy = CatalogMediaAuthorizationPolicies.Manage)]
            [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
            [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status201Created)]
            [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
            public async Task<ActionResult<CatalogMediaResponse>> RegisterAsync(
                [FromBody] RegisterCatalogMediaRequest request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);
                var result = await service.RegisterAsync(
                    request,
                    CatalogMediaHttpContext.CreateCommandContext(HttpContext),
                    CatalogMediaHttpContext.RequireIdempotencyKey(Request),
                    cancellationToken);
                return result.Replayed
                    ? Ok(result.Response)
                    : StatusCode(StatusCodes.Status201Created, result.Response);
            }

            [HttpPost("{assetId:guid}/upload-authorizations", Name = CatalogMediaOperationIds.PrepareUpload)]
            [Authorize(Policy = CatalogMediaAuthorizationPolicies.Manage)]
            [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
            [ProducesResponseType<CatalogMediaUploadAuthorizationResponse>(StatusCodes.Status200OK)]
            public async Task<ActionResult<CatalogMediaUploadAuthorizationResponse>> PrepareUploadAsync(
                Guid assetId,
                [FromBody] PrepareCatalogMediaUploadRequest request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);
                var result = await service.PrepareUploadAsync(
                    assetId,
                    request,
                    CatalogMediaHttpContext.CreateCommandContext(HttpContext),
                    CatalogMediaHttpContext.RequireIdempotencyKey(Request),
                    cancellationToken);
                return Ok(result.Response);
            }

            [HttpPost("{assetId:guid}/upload-completions", Name = CatalogMediaOperationIds.CompleteUpload)]
            [Authorize(Policy = CatalogMediaAuthorizationPolicies.Manage)]
            [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
            [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
            public async Task<ActionResult<CatalogMediaResponse>> CompleteUploadAsync(
                Guid assetId,
                [FromBody] CompleteCatalogMediaUploadRequest request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);
                var result = await service.CompleteUploadAsync(
                    assetId,
                    request,
                    CatalogMediaHttpContext.CreateCommandContext(HttpContext),
                    CatalogMediaHttpContext.RequireIdempotencyKey(Request),
                    cancellationToken);
                return Ok(result.Response);
            }

            [HttpPost("{assetId:guid}/rights-revocations", Name = CatalogMediaOperationIds.RevokeRights)]
            [Authorize(Policy = CatalogMediaAuthorizationPolicies.RevokeRights)]
            [EnableRateLimiting(CatalogMediaRateLimitPolicies.Commands)]
            [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
            public async Task<ActionResult<CatalogMediaResponse>> RevokeRightsAsync(
                Guid assetId,
                [FromBody] RevokeCatalogMediaRightsRequest request,
                CancellationToken cancellationToken)
            {
                ArgumentNullException.ThrowIfNull(request);
                var result = await service.RevokeRightsAsync(
                    assetId,
                    request,
                    CatalogMediaHttpContext.CreateCommandContext(HttpContext),
                    CatalogMediaHttpContext.RequireIdempotencyKey(Request),
                    cancellationToken);
                return Ok(result.Response);
            }

            [HttpGet("{assetId:guid}", Name = CatalogMediaOperationIds.Get)]
            [Authorize(Policy = CatalogMediaAuthorizationPolicies.Read)]
            [EnableRateLimiting(CatalogMediaRateLimitPolicies.Reads)]
            [ProducesResponseType<CatalogMediaResponse>(StatusCodes.Status200OK)]
            public async Task<ActionResult<CatalogMediaResponse>> GetAsync(
                Guid assetId,
                CancellationToken cancellationToken) =>
                Ok(await service.GetAsync(assetId, cancellationToken));
        }
        """;

    private static string ProgramSource() =>
        """
        using System.Text.Json;
        using System.Text.Json.Serialization;
        using Aggregator.CatalogMedia.Api;
        using Aggregator.CatalogMedia.Application;
        using Aggregator.CatalogMedia.Infrastructure;
        using Microsoft.AspNetCore.Mvc;
        using Microsoft.AspNetCore.RateLimiting;
        using Platform.ObjectStorage;
        using Platform.Observability;
        using Platform.ProblemDetails;
        using Platform.Security;

        namespace Aggregator.CatalogMedia.Api;

        public partial class Program
        {
            public static void Main(string[] args) => CreateApplication(args).Run();

            public static WebApplication CreateApplication(string[] args)
            {
                ArgumentNullException.ThrowIfNull(args);
                var builder = WebApplication.CreateBuilder(args);
                builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 256 * 1024);
                builder.Services.AddControllers().AddJsonOptions(options =>
                {
                    options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                    options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
                    options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                    options.JsonSerializerOptions.Converters.Add(
                        new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
                });
                builder.Services.Configure<ApiBehaviorOptions>(options =>
                    options.InvalidModelStateResponseFactory = _ => throw new OwnerException(new OwnerError(
                        "CatalogMedia.Transport",
                        "CATALOG_MEDIA_REQUEST_CONTRACT_INVALID",
                        "Catalog media request contract is invalid",
                        StatusCodes.Status400BadRequest,
                        "The request cannot be bound to the active media contract.",
                        "Correct the JSON fields and use only declared string enum tokens.")));
                builder.Services.AddOpenApi("catalog-media");
                builder.Services.AddOwnerProblemDetails();
                builder.Services.AddCatalogMediaApplication();
                AddObjectStore(builder);
                builder.Services.AddCatalogMediaInfrastructure(builder.Configuration);
                builder.Services.AddPlatformObservability(builder.Configuration, "catalog-media-api");
                builder.Services.AddRateLimiter(options =>
                {
                    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                    options.AddFixedWindowLimiter(CatalogMediaRateLimitPolicies.Commands, limiter =>
                    {
                        limiter.PermitLimit = 60;
                        limiter.QueueLimit = 0;
                        limiter.Window = TimeSpan.FromMinutes(1);
                        limiter.AutoReplenishment = true;
                    });
                    options.AddFixedWindowLimiter(CatalogMediaRateLimitPolicies.Reads, limiter =>
                    {
                        limiter.PermitLimit = 180;
                        limiter.QueueLimit = 0;
                        limiter.Window = TimeSpan.FromMinutes(1);
                        limiter.AutoReplenishment = true;
                    });
                });
                var authorization = builder.Services.AddPlatformJwtAuthentication(
                    builder.Configuration,
                    audience: "aggregator-catalog-media");
                authorization
                    .AddRequiredScopePolicy(CatalogMediaAuthorizationPolicies.Manage, CatalogMediaAuthorizationPolicies.Manage)
                    .AddRequiredScopePolicy(CatalogMediaAuthorizationPolicies.Read, CatalogMediaAuthorizationPolicies.Read)
                    .AddRequiredScopePolicy(CatalogMediaAuthorizationPolicies.RevokeRights, CatalogMediaAuthorizationPolicies.RevokeRights)
                    .AddRequiredScopePolicy(CatalogMediaAuthorizationPolicies.TestContracts, CatalogMediaAuthorizationPolicies.TestContracts);

                var app = builder.Build();
                app.UseOwnerProblemDetails();
                app.UseMiddleware<CatalogMediaFailureMiddleware>();
                app.UseRateLimiter();
                app.UseAuthentication();
                app.UseAuthorization();
                app.MapGet("/health/live", () => Results.Ok(new { owner = "CatalogMedia.Runtime", state = "live" }))
                    .AllowAnonymous();
                app.MapGet("/health/ready", async (
                    CatalogMediaReadinessProbe readiness,
                    CancellationToken cancellationToken) =>
                {
                    var ready = await readiness.CanConnectAsync(cancellationToken);
                    return ready
                        ? Results.Ok(new { owner = "CatalogMedia.Persistence", state = "ready" })
                        : Results.Problem(
                            title: "Catalog media database unavailable",
                            statusCode: StatusCodes.Status503ServiceUnavailable,
                            detail: "The media API cannot reach catalog_db.");
                }).AllowAnonymous();
                app.MapControllers();
                if (app.Environment.IsDevelopment())
                {
                    app.MapOpenApi("/openapi/{documentName}.json")
                        .RequireAuthorization(CatalogMediaAuthorizationPolicies.TestContracts);
                }
                return app;
            }

            private static void AddObjectStore(WebApplicationBuilder builder)
            {
                var options = new S3ObjectStoreOptions
                {
                    ServiceUrl = new Uri(Require(builder.Configuration, "CatalogMedia:ObjectStorage:ServiceUrl"), UriKind.Absolute),
                    Region = builder.Configuration["CatalogMedia:ObjectStorage:Region"] ?? "us-east-1",
                    Bucket = Require(builder.Configuration, "CatalogMedia:ObjectStorage:Bucket"),
                    AccessKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:AccessKey"),
                    SecretKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:SecretKey"),
                    ForcePathStyle = bool.TryParse(builder.Configuration["CatalogMedia:ObjectStorage:ForcePathStyle"], out var force)
                        ? force : true,
                };
                options.Validate();
                builder.Services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(options));
            }

            private static string Require(IConfiguration configuration, string path) =>
                configuration[path] is { Length: > 0 } value
                    ? value.Trim()
                    : throw new InvalidOperationException($"Configuration value '{path}' is required.");
        }
        """;
}
