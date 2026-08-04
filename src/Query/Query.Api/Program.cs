using System.Text.Json;
using System.Text.Json.Serialization;
using Aggregator.Query.Api;
using Aggregator.Query.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Platform.Observability;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("Query");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("ConnectionStrings:Query is required.");
}

builder.Services
    .AddQueryDatabase(new QueryDatabaseOptions
    {
        ConnectionString = connectionString,
    })
    .AddQueryPublicReadInfrastructure()
    .AddQueryPromotionOverlayProjection();
builder.Services.AddPlatformObservability(builder.Configuration, "catalog-query-api");
builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        options.JsonSerializerOptions.Converters.Add(
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
    });
builder.Services.AddOpenApi("catalog-query");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("public-query", limiter =>
    {
        limiter.PermitLimit = 120;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
        limiter.AutoReplenishment = true;
    });
});

var app = builder.Build();
app.UseMiddleware<QueryFailureMiddleware>();
app.UseRateLimiter();
app.MapControllers();
app.MapGet("/health/live", () => Results.Ok(new
{
    owner = "Query.Runtime",
    state = "live",
}));
app.MapGet("/health/ready", async (
    QueryReadinessProbe readinessProbe,
    CancellationToken cancellationToken) =>
{
    var snapshot = await readinessProbe.CheckAsync(cancellationToken);
    var statusCode = snapshot.State == QueryReadinessState.Ready
        ? StatusCodes.Status200OK
        : StatusCodes.Status503ServiceUnavailable;
    return Results.Json(snapshot, statusCode: statusCode);
});
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi("/openapi/{documentName}.json");
}

await app.RunAsync();

public partial class Program;
