using System.Threading.RateLimiting;
using Aggregator.Ingestion.Api;
using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;
using Platform.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .ConfigureApiBehaviorOptions(options =>
        options.InvalidModelStateResponseFactory = IngestionModelStateProblemFactory.Create);
builder.Services.Configure<JsonOptions>(options =>
    IngestionApiJson.Configure(options.SerializerOptions));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();
builder.Services.AddIngestionApplication();
builder.Services.AddIngestionInfrastructure(builder.Configuration);
builder.Services.AddIngestionObjectStorage(builder.Configuration);
builder.Services.AddPlatformObservability(builder.Configuration, "ingestion-api");

var authority = builder.Configuration["Authentication:Authority"];
if (string.IsNullOrWhiteSpace(authority))
{
    throw new InvalidOperationException("Authentication:Authority is required.");
}

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = authority;
        options.RequireHttpsMetadata = builder.Configuration.GetValue("Authentication:RequireHttpsMetadata", true);
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = false,
            NameClaimType = "sub",
        };
        options.Events = new JwtBearerEvents
        {
            OnChallenge = context =>
                IngestionAuthorizationStatusCodeWriter.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status401Unauthorized,
                    "AUTHENTICATION_REQUIRED",
                    "A valid bearer token is required.",
                    "Provide a valid service access token.",
                    context.RequestAborted),
            OnForbidden = context =>
                IngestionAuthorizationStatusCodeWriter.WriteAsync(
                    context.HttpContext,
                    StatusCodes.Status403Forbidden,
                    "AUTHORIZATION_DENIED",
                    "The caller lacks the required Ingestion scope.",
                    "Acquire the exact operation scope before retrying.",
                    context.RequestAborted),
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(
        IngestionAuthorizationPolicies.Submit,
        policy => policy.RequireClaim("scope", IngestionScopes.Submit));
    options.AddPolicy(
        IngestionAuthorizationPolicies.Read,
        policy => policy.RequireClaim("scope", IngestionScopes.Read));
    options.AddPolicy(
        IngestionAuthorizationPolicies.Review,
        policy => policy.RequireClaim("scope", IngestionScopes.Review));
    options.AddPolicy(
        IngestionAuthorizationPolicies.Commit,
        policy => policy.RequireClaim("scope", IngestionScopes.Commit));
    options.AddPolicy(
        IngestionAuthorizationPolicies.DeliverCatalog,
        policy => policy.RequireClaim("scope", IngestionScopes.DeliverCatalog));
    options.AddPolicy(
        IngestionAuthorizationPolicies.ManageProducers,
        policy => policy.RequireClaim("scope", IngestionScopes.ManageProducers));
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        var key = context.User.FindFirst("sub")?.Value
            ?? context.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";
        return RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = 120,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
            });
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseMiddleware<IngestionFailureMiddleware>();
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength is > 1_048_576)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(
            new
            {
                owner = "Ingestion.Transport",
                code = "REQUEST_TOO_LARGE",
                detail = "The request body exceeds the Ingestion API limit.",
                requiredAction = "Upload large package payloads through the object-storage upload contract.",
            },
            cancellationToken: context.RequestAborted);
        return;
    }

    await next(context);
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapIngestionHealthEndpoints();
app.MapOpenApi().RequireAuthorization();
app.MapControllers();
app.Run();

public partial class Program;
