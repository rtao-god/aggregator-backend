using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;
using Platform.ProblemDetails;
using Platform.Security;

namespace Aggregator.Ingestion.Api;

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
            options.InvalidModelStateResponseFactory = IngestionModelStateProblemFactory.Create);
        builder.Services.AddOpenApi("ingestion");
        builder.Services.AddOwnerProblemDetails();
        builder.Services.AddIngestionApplication();
        builder.Services.AddIngestionInfrastructure(builder.Configuration);
        builder.Services.AddIngestionObjectStorage(builder.Configuration);
        builder.Services.AddIngestionProcessingInfrastructure(builder.Configuration);
        builder.Services.AddPlatformObservability(builder.Configuration, "ingestion-api");
        builder.Services.AddRateLimiter(options =>
            options.AddFixedWindowLimiter(
                IngestionRateLimitPolicies.BatchCommands,
                limiter =>
                {
                    limiter.PermitLimit = 30;
                    limiter.QueueLimit = 0;
                    limiter.Window = TimeSpan.FromMinutes(1);
                    limiter.AutoReplenishment = true;
                }));

        var authorization = builder.Services.AddPlatformJwtAuthentication(
            builder.Configuration,
            audience: "aggregator-ingestion");
        authorization
            .AddRequiredScopePolicy(
                IngestionAuthorizationPolicies.Upload,
                IngestionAuthorizationPolicies.Upload)
            .AddRequiredScopePolicy(
                IngestionAuthorizationPolicies.Read,
                IngestionAuthorizationPolicies.Read)
            .AddRequiredScopePolicy(
                IngestionAuthorizationPolicies.TestContracts,
                IngestionAuthorizationPolicies.TestContracts)
            .AddRequiredScopePolicy(
                IngestionProcessingAuthorizationPolicies.Review,
                IngestionProcessingAuthorizationPolicies.Review)
            .AddRequiredScopePolicy(
                IngestionProcessingAuthorizationPolicies.Commit,
                IngestionProcessingAuthorizationPolicies.Commit)
            .AddRequiredScopePolicy(
                IngestionProcessingAuthorizationPolicies.Delivery,
                IngestionProcessingAuthorizationPolicies.Delivery);

        var application = builder.Build();
        application.UseOwnerProblemDetails();
        application.UseStatusCodePages(IngestionAuthorizationStatusCodeWriter.WriteAsync);
        application.UseMiddleware<IngestionFailureMiddleware>();
        application.UseRateLimiter();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapGet("/health/live", IngestionHealthEndpoints.Live)
            .AllowAnonymous()
            .WithName("IngestionLive");
        application.MapGet("/health/ready", IngestionHealthEndpoints.ReadyAsync)
            .AllowAnonymous()
            .WithName("IngestionReady");
        application.MapControllers();
        if (application.Environment.IsDevelopment())
        {
            application.MapOpenApi("/openapi/{documentName}.json")
                .RequireAuthorization(IngestionAuthorizationPolicies.TestContracts);
        }

        return application;
    }
}
