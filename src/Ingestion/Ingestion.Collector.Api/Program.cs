using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Ingestion.Collector.Api;
using Aggregator.Ingestion.Collector.Application;
using Aggregator.Ingestion.Collector.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;
using Platform.Security;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 128 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Ingestion");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Ingestion is required.");
}

var collectorOptions = new CollectorCandidateOptions();
collectorOptions.Validate();
builder.Services.AddCollectorCandidateInfrastructure(connectionString);
builder.Services.AddSingleton(collectorOptions);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<CollectorCandidateService>();
builder.Services.AddPlatformObservability(builder.Configuration, "ingestion-collector-api");
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    });
builder.Services.AddOpenApi("ingestion-collector");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("collector-intake", limiter =>
    {
        limiter.PermitLimit = 120;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});
var authorization = builder.Services.AddPlatformJwtAuthentication(
    builder.Configuration,
    audience: "aggregator-ingestion-command");
authorization.AddRequiredScopePolicy("ingestion.submit", "ingestion.submit");

var app = builder.Build();
app.UseMiddleware<CollectorCandidateFailureMiddleware>();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new
{
    owner = "Ingestion.Collector",
    state = "live",
})).AllowAnonymous();
app.MapGet("/health/ready", async (
    ICollectorCandidateStore store,
    CancellationToken cancellationToken) =>
{
    var ready = await store.CheckReadinessAsync(cancellationToken);
    return Results.Json(
        new
        {
            owner = "Ingestion.Collector",
            state = ready ? "ready" : "unavailable",
        },
        statusCode: ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json")
        .RequireAuthorization("ingestion.submit");
}

await app.RunAsync();

public partial class Program;
