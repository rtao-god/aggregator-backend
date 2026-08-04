using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Infrastructure;
using Microsoft.AspNetCore.Mvc;
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
        builder.Services.AddPlatformObservability(builder.Configuration, "catalog-command-api");
        builder.Services.AddRateLimiter(options =>
            options.AddFixedWindowLimiter(
                CatalogRateLimitPolicies.Command,
                limiter =>
                {
                    limiter.PermitLimit = 60;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                }));

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
                CatalogAuthorizationPolicies.SubmitClaim,
                CatalogAuthorizationPolicies.SubmitClaim)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.VerifyClaim,
                CatalogAuthorizationPolicies.VerifyClaim)
            .AddRequiredScopePolicy(
                CatalogAuthorizationPolicies.TestContracts,
                CatalogAuthorizationPolicies.TestContracts);

        var application = builder.Build();
        application.UseOwnerProblemDetails();
        application.UseStatusCodePages(CatalogAuthorizationStatusCodeWriter.WriteAsync);
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
}
