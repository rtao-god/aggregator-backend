using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Promotion.Overlay.Api;
using Aggregator.Promotion.Overlay.Application;
using Aggregator.Promotion.Overlay.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;
using Platform.Security;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 256 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Promotion");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Promotion is required.");
}

builder.Services.AddPromotionOverlayInfrastructure(connectionString);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<PromotionOverlayPublicationService>();
builder.Services.AddScoped<PromotionOverlayReadinessProbe>();
builder.Services.AddPlatformObservability(builder.Configuration, "promotion-overlay-api");
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    });
builder.Services.AddOpenApi("promotion-overlay-command");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter(
        PromotionOverlayRateLimitPolicies.Command,
        limiter =>
        {
            limiter.PermitLimit = 30;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
});
var authorization = builder.Services.AddPlatformJwtAuthentication(
    builder.Configuration,
    audience: "aggregator-promotion-command");
authorization.AddRequiredScopePolicy(
    PromotionOverlayAuthorizationPolicies.Publish,
    PromotionOverlayAuthorizationPolicies.Publish);

var app = builder.Build();
app.UseMiddleware<PromotionOverlayFailureMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new
{
    owner = "Promotion.Overlay",
    state = "live",
})).AllowAnonymous();
app.MapGet("/health/ready", async (
    PromotionOverlayReadinessProbe probe,
    CancellationToken cancellationToken) =>
{
    var ready = await probe.CheckAsync(cancellationToken);
    return Results.Json(
        new
        {
            owner = "Promotion.Overlay",
            state = ready ? "ready" : "unavailable",
        },
        statusCode: ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json")
        .RequireAuthorization(PromotionOverlayAuthorizationPolicies.Publish);
}

await app.RunAsync();

public partial class Program;
