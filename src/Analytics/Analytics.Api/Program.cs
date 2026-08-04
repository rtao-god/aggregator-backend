using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Analytics.Api;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 64 * 1024);
var connectionString = builder.Configuration.GetConnectionString("Analytics");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Analytics is required.");
}

var sessionHashKeyText = builder.Configuration["Analytics:SessionHashKey"];
if (string.IsNullOrWhiteSpace(sessionHashKeyText))
{
    throw new InvalidOperationException("Analytics:SessionHashKey is required.");
}

byte[] sessionHashKey;
try
{
    sessionHashKey = Convert.FromBase64String(sessionHashKeyText);
}
catch (FormatException exception)
{
    throw new InvalidOperationException(
        "Analytics:SessionHashKey must be a base64-encoded secret.",
        exception);
}

var runtimeOptions = new AnalyticsRuntimeOptions
{
    SessionHashKey = sessionHashKey,
};
runtimeOptions.Validate();
var apiOptions = new AnalyticsApiOptions
{
    InternalMetricsKey = builder.Configuration["Analytics:InternalMetricsKey"]
        ?? throw new InvalidOperationException("Analytics:InternalMetricsKey is required."),
};
apiOptions.Validate();

builder.Services.AddAnalyticsRuntimeInfrastructure(connectionString);
builder.Services.AddSingleton(runtimeOptions);
builder.Services.AddSingleton(apiOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<AnalyticsRuntimeService>();
builder.Services.AddPlatformObservability(builder.Configuration, "analytics-api");
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    });
builder.Services.AddOpenApi("analytics-public");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("analytics-intake", limiter =>
    {
        limiter.PermitLimit = 240;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
app.UseMiddleware<AnalyticsFailureMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new
{
    owner = "Analytics.Runtime",
    state = "live",
}));
app.MapGet("/health/ready", async (
    IAnalyticsRuntimeStore store,
    CancellationToken cancellationToken) =>
{
    var ready = await store.CheckReadinessAsync(cancellationToken);
    return Results.Json(
        new
        {
            owner = "Analytics.Runtime",
            state = ready ? "ready" : "unavailable",
        },
        statusCode: ready
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable);
});
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

await app.RunAsync();

public partial class Program;
