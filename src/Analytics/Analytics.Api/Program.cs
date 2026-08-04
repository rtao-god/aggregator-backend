using System.Text.Json;
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
            options.Limits.MaxRequestBodySize = 64 * 1024);

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
            options.InvalidModelStateResponseFactory = AnalyticsModelStateProblemFactory.Create);
        builder.Services.AddOpenApi("analytics");
        builder.Services.AddOwnerProblemDetails();
        builder.Services.AddAnalyticsApplication();
        builder.Services.AddAnalyticsInfrastructure(builder.Configuration);
        builder.Services.AddPlatformObservability(builder.Configuration, "analytics-api");

        var antiAbuseOptions = AnalyticsAntiAbuseOptions.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(antiAbuseOptions);
        builder.Services.AddSingleton<AnalyticsAntiAbuseProofService>();
        builder.Services.AddSingleton<IAntiAbuseVerifier>(services =>
            services.GetRequiredService<AnalyticsAntiAbuseProofService>());

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddFixedWindowLimiter(
                AnalyticsRateLimitPolicies.AntiAbuseTokens,
                limiter =>
                {
                    limiter.PermitLimit = 60;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                });
            options.AddFixedWindowLimiter(
                AnalyticsRateLimitPolicies.InteractionEvents,
                limiter =>
                {
                    limiter.PermitLimit = 240;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                });
            options.AddFixedWindowLimiter(
                AnalyticsRateLimitPolicies.Metrics,
                limiter =>
                {
                    limiter.PermitLimit = 120;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                });
        });

        var authorization = builder.Services.AddPlatformJwtAuthentication(
            builder.Configuration,
            audience: "aggregator-analytics");
        authorization
            .AddRequiredScopePolicy(
                AnalyticsAuthorizationPolicies.ViewListing,
                AnalyticsAuthorizationPolicies.ViewListing)
            .AddRequiredScopePolicy(
                AnalyticsAuthorizationPolicies.TestContracts,
                AnalyticsAuthorizationPolicies.TestContracts);

        var application = builder.Build();
        application.UseOwnerProblemDetails();
        application.UseStatusCodePages(AnalyticsAuthorizationStatusCodeWriter.WriteAsync);
        application.UseMiddleware<AnalyticsFailureMiddleware>();
        application.UseRateLimiter();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/health/live", AnalyticsHealthEndpoints.Live)
            .AllowAnonymous()
            .WithName("AnalyticsLive");
        application.MapGet("/health/ready", AnalyticsHealthEndpoints.ReadyAsync)
            .AllowAnonymous()
            .WithName("AnalyticsReady");
        application.MapControllers();
        if (application.Environment.IsDevelopment())
        {
            application.MapOpenApi("/openapi/{documentName}.json")
                .RequireAuthorization(AnalyticsAuthorizationPolicies.TestContracts);
        }

        return application;
    }
}
