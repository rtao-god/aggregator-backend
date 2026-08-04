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

    private static string Require(Microsoft.Extensions.Configuration.ConfigurationManager configuration, string path) =>
        configuration[path] is { Length: > 0 } value
            ? value.Trim()
            : throw new InvalidOperationException($"Configuration value '{path}' is required.");
}
