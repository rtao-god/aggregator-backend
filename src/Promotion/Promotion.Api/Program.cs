using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Promotion.Application;
using Aggregator.Promotion.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;
using Platform.ProblemDetails;
using Platform.Security;

namespace Aggregator.Promotion.Api;

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
            options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
        builder.Services
            .AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                options.JsonSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(
                        JsonNamingPolicy.CamelCase,
                        allowIntegerValues: false));
            });
        builder.Services.Configure<ApiBehaviorOptions>(options =>
            options.InvalidModelStateResponseFactory = PromotionModelStateProblemFactory.Create);
        builder.Services.AddOpenApi("promotion");
        builder.Services.AddOwnerProblemDetails();
        builder.Services.AddPromotionApplication();
        builder.Services.AddPromotionInfrastructure(builder.Configuration);
        builder.Services.AddPlatformObservability(builder.Configuration, "promotion-api");
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter(
                PromotionRateLimitPolicies.Commands,
                limiter =>
                {
                    limiter.PermitLimit = 60;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                });
            options.AddFixedWindowLimiter(
                PromotionRateLimitPolicies.Reads,
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
            audience: "aggregator-promotion");
        authorization
            .AddRequiredScopePolicy(
                PromotionAuthorizationPolicies.ManageListing,
                PromotionAuthorizationPolicies.ManageListing)
            .AddRequiredScopePolicy(
                PromotionAuthorizationPolicies.ManageCatalog,
                PromotionAuthorizationPolicies.ManageCatalog)
            .AddRequiredScopePolicy(
                PromotionAuthorizationPolicies.Read,
                PromotionAuthorizationPolicies.Read)
            .AddRequiredScopePolicy(
                PromotionAuthorizationPolicies.TestContracts,
                PromotionAuthorizationPolicies.TestContracts);

        var application = builder.Build();
        application.UseOwnerProblemDetails();
        application.UseStatusCodePages(PromotionAuthorizationStatusCodeWriter.WriteAsync);
        application.UseMiddleware<PromotionFailureMiddleware>();
        application.UseRateLimiter();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/health/live", PromotionHealthEndpoints.Live)
            .AllowAnonymous()
            .WithName("PromotionLive");
        application.MapGet("/health/ready", PromotionHealthEndpoints.ReadyAsync)
            .AllowAnonymous()
            .WithName("PromotionReady");
        application.MapControllers();
        if (application.Environment.IsDevelopment())
        {
            application.MapOpenApi("/openapi/{documentName}.json")
                .RequireAuthorization(PromotionAuthorizationPolicies.TestContracts);
        }

        return application;
    }
}
