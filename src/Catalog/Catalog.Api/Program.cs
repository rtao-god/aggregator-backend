using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Infrastructure;
using Aggregator.Catalog.Media.Application;
using Aggregator.Catalog.Media.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.ObjectStorage;
using Platform.Observability;
using Platform.ProblemDetails;
using Platform.Security;

namespace Aggregator.Catalog.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var application = CreateApplication(args);
        application.Run();
    }

    public static WebApplication CreateApplication(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options =>
            options.Limits.MaxRequestBodySize = 8 * 1024 * 1024);
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.PropertyNameCaseInsensitive = false;
                options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                options.JsonSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(
                        JsonNamingPolicy.CamelCase,
                        allowIntegerValues: false));
            });
        builder.Services.Configure<ApiBehaviorOptions>(options =>
            options.InvalidModelStateResponseFactory = CatalogModelStateProblemFactory.Create);
        builder.Services.AddOpenApi("catalog-command");
        builder.Services.AddOwnerProblemDetails();
        builder.Services.AddCatalogApplication();
        builder.Services.AddCatalogInfrastructure(builder.Configuration);
        builder.Services.AddCatalogMediaApplication();
        AddCatalogMediaObjectStore(builder);
        builder.Services.AddCatalogMediaInfrastructure(builder.Configuration);
        builder.Services.AddCatalogIngestionInfrastructure(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<CatalogIngestionDraftService>();
        builder.Services.AddScoped<ICatalogIngestionDraftCommandHandler, VerifiedCatalogIngestionDraftService>();
        builder.Services.AddPlatformObservability(builder.Configuration, "catalog-command-api");
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(
                CatalogRateLimitPolicies.Command,
                limiter =>
                {
                    limiter.PermitLimit = 60;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                });
            options.AddFixedWindowLimiter(
                CatalogMediaRateLimitPolicies.Commands,
                limiter =>
                {
                    limiter.PermitLimit = 60;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                });
            options.AddFixedWindowLimiter(
                CatalogMediaRateLimitPolicies.Reads,
                limiter =>
                {
                    limiter.PermitLimit = 180;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                });
        });

        var authorization = builder.Services.AddPlatformJwtAuthentication(
            builder.Configuration,
            audience: "aggregator-catalog-command");
        authorization
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.ManageConfiguration,
                CatalogAuthorizationPolicies.ManageConfiguration)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.EditListing,
                CatalogAuthorizationPolicies.EditListing)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.Publish,
                CatalogAuthorizationPolicies.Publish)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.Rollback,
                CatalogAuthorizationPolicies.Rollback)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.ManageVisibility,
                CatalogAuthorizationPolicies.ManageVisibility)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.SubmitClaim,
                CatalogAuthorizationPolicies.SubmitClaim)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.VerifyClaim,
                CatalogAuthorizationPolicies.VerifyClaim)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.TestContracts,
                CatalogAuthorizationPolicies.TestContracts)
            .AddRequiredScopePolicy(
                CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand,
                CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand)
            .AddRequiredScopePolicy(
                CatalogMediaAuthorizationPolicies.Manage,
                CatalogMediaAuthorizationPolicies.Manage)
            .AddRequiredScopePolicy(
                CatalogMediaAuthorizationPolicies.Read,
                CatalogMediaAuthorizationPolicies.Read)
            .AddRequiredScopePolicy(
                CatalogMediaAuthorizationPolicies.RevokeRights,
                CatalogMediaAuthorizationPolicies.RevokeRights);

        var application = builder.Build();
        application.UseOwnerProblemDetails();
        application.UseStatusCodePages(CatalogAuthorizationStatusCodeWriter.WriteAsync);
        application.UseMiddleware<CatalogMediaFailureMiddleware>();
        application.UseMiddleware<CatalogIngestionFailureMiddleware>();
        application.UseMiddleware<CatalogFailureMiddleware>();
        application.UseRateLimiter();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/health/live", CatalogHealthEndpoints.Live)
            .AllowAnonymous()
            .WithName("CatalogLive");
        application.MapGet("/health/ready", CatalogHealthEndpoints.ReadyAsync)
            .AllowAnonymous()
            .WithName("CatalogReady");
        application.MapControllers();
        if (application.Environment.IsDevelopment())
        {
            application.MapOpenApi("/openapi/{documentName}.json")
                .RequireAuthorization(CatalogAuthorizationPolicies.TestContracts);
        }

        return application;
    }

    private static void AddCatalogMediaObjectStore(WebApplicationBuilder builder)
    {
        var options = new S3ObjectStoreOptions
        {
            ServiceUrl = new Uri(Require(builder.Configuration, "CatalogMedia:ObjectStorage:ServiceUrl"), UriKind.Absolute),
            Region = builder.Configuration["CatalogMedia:ObjectStorage:Region"] ?? "us-east-1",
            Bucket = Require(builder.Configuration, "CatalogMedia:ObjectStorage:Bucket"),
            AccessKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:AccessKey"),
            SecretKey = Require(builder.Configuration, "CatalogMedia:ObjectStorage:SecretKey"),
            ForcePathStyle = bool.TryParse(
                builder.Configuration["CatalogMedia:ObjectStorage:ForcePathStyle"],
                out var forcePathStyle)
                ? forcePathStyle
                : true,
        };
        options.Validate();
        builder.Services.AddSingleton<IObjectStore>(_ => new S3ObjectStore(options));
    }

    private static string Require(
        Microsoft.Extensions.Configuration.ConfigurationManager configuration,
        string path) =>
        configuration[path] is { Length: > 0 } value
            ? value.Trim()
            : throw new InvalidOperationException($"Configuration value '{path}' is required.");
}
