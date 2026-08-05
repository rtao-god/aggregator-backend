using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Acceptance.Contracts;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
var internalKey = builder.Configuration["Acceptance:InternalKey"];
if (string.IsNullOrWhiteSpace(internalKey) || internalKey.Length < 32)
{
    throw new InvalidOperationException(
        "Acceptance:InternalKey must contain at least 32 characters.");
}

builder.Services.AddAnalyticsApplication();
builder.Services.AddAnalyticsInfrastructure(builder.Configuration);

var app = builder.Build();
app.MapGet("/health/live", () => Results.Ok(new
{
    owner = "Acceptance.Analytics.Control",
    state = "live",
}));
app.MapGet("/health/ready", async (
    AnalyticsReadinessProbe readinessProbe,
    CancellationToken cancellationToken) =>
{
    try
    {
        return await readinessProbe.CanConnectAsync(cancellationToken)
            ? Results.Ok(new { owner = "Acceptance.Analytics.Control", state = "ready" })
            : Results.Json(
                new { owner = "Acceptance.Analytics.Control", state = "database_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        return Results.Json(
            new
            {
                owner = "Acceptance.Analytics.Control",
                state = "database_unavailable",
                failureType = exception.GetType().Name,
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapPost("/acceptance/analytics/bootstrap", async (
    HttpRequest httpRequest,
    AnalyticsBootstrapRequest request,
    IPublicReadReferenceProjectionWriter publicReadWriter,
    IListingMetricsAccessProjectionWriter accessWriter,
    CancellationToken cancellationToken) =>
{
    if (!HasValidInternalKey(httpRequest, internalKey))
    {
        return Results.Unauthorized();
    }

    var publicReadDigest = ComputeDigest(
        request.PublicReadRevisionId.ToString("D"),
        request.CatalogKey,
        request.BaseProjectionId.ToString("D"),
        request.PromotionOverlayId.ToString("D"),
        request.SafetyOverlayId.ToString("D"),
        request.SourcePublicationId.ToString("D"));
    var membershipDigest = ComputeDigest(
        request.PublicReadRevisionId.ToString("D"),
        request.ListingId.ToString("D"));
    var accessDigest = ComputeDigest(
        request.ListingId.ToString("D"),
        request.ActorId.ToString("D"),
        request.AccessSourceRevision.ToString(CultureInfo.InvariantCulture));

    await publicReadWriter.ApplyAsync(
        PublicReadReferenceProjection.Create(
            request.PublicReadRevisionId,
            request.CatalogKey,
            request.BaseProjectionId,
            request.PromotionOverlayId,
            request.SafetyOverlayId,
            request.SourcePublicationId,
            publicReadDigest,
            membershipDigest,
            request.ActivatedAtUtc,
            [request.ListingId]),
        cancellationToken);
    await accessWriter.ApplyAsync(
        ListingMetricsAccessProjection.Create(
            request.ListingId,
            request.ActorId,
            true,
            request.AccessSourceRevision,
            accessDigest,
            request.ActivatedAtUtc),
        cancellationToken);

    return Results.Ok(new AnalyticsBootstrapResponse(
        request.PublicReadRevisionId,
        request.ListingId,
        request.ActorId));
});
app.MapPost("/acceptance/analytics/rebuild", async (
    HttpRequest httpRequest,
    AnalyticsRebuildRequest request,
    RebuildDailyAnalyticsMetricsService rebuildService,
    CancellationToken cancellationToken) =>
{
    if (!HasValidInternalKey(httpRequest, internalKey))
    {
        return Results.Unauthorized();
    }

    var result = await rebuildService.RebuildAsync(
        new RebuildDailyAnalyticsMetricsRequest(
            request.FromInclusive,
            request.ToExclusive),
        cancellationToken);
    return Results.Ok(new AnalyticsRebuildResponse(
        result.FromInclusive,
        result.ToExclusive,
        result.MaterializedMetricCount,
        result.RemovedStaleMetricCount,
        result.CompletedAtUtc));
});

await app.RunAsync();

static bool HasValidInternalKey(HttpRequest request, string expectedKey)
{
    if (!request.Headers.TryGetValue("X-Acceptance-Key", out var suppliedValues))
    {
        return false;
    }

    var suppliedKey = suppliedValues.ToString();
    if (string.IsNullOrWhiteSpace(suppliedKey))
    {
        return false;
    }

    var expectedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(expectedKey));
    var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(suppliedKey));
    return CryptographicOperations.FixedTimeEquals(expectedDigest, suppliedDigest);
}

static string ComputeDigest(params string[] components)
{
    ArgumentNullException.ThrowIfNull(components);
    return Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', components))));
}

public partial class Program;
