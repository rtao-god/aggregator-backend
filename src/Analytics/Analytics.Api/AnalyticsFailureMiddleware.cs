using System.Diagnostics;
using System.Text.Json;
using Aggregator.Analytics.Application;

namespace Aggregator.Analytics.Api;

public sealed class AnalyticsFailureMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<AnalyticsFailureMiddleware> _logger;

    public AnalyticsFailureMiddleware(
        RequestDelegate next,
        ILogger<AnalyticsFailureMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await _next(context);
        }
        catch (AnalyticsRuntimeException exception) when (!context.Response.HasStarted)
        {
            await WriteAsync(
                context,
                exception.Code,
                exception.StatusCode,
                exception.Message,
                exception.RequiredAction,
                exception.Context,
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !context.Response.HasStarted)
        {
            await WriteAsync(
                context,
                "ANALYTICS_UNHANDLED_FAILURE",
                StatusCodes.Status500InternalServerError,
                "Analytics request processing failed before a typed owner result was produced.",
                "Inspect the correlated server diagnostic and correct the Analytics owner.",
                new Dictionary<string, object?>(StringComparer.Ordinal),
                exception);
        }
    }

    private async Task WriteAsync(
        HttpContext context,
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        IReadOnlyDictionary<string, object?> failureContext,
        Exception exception)
    {
        var correlationId = Activity.Current?.TraceId.ToString()
            ?? Guid.CreateVersion7().ToString("D");
        _logger.LogError(
            exception,
            "Analytics failure {Code} for correlation {CorrelationId}",
            code,
            correlationId);
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            new
            {
                type = $"https://errors.example/analytics/{code.Replace('_', '-').ToLowerInvariant()}",
                title = detail,
                status = statusCode,
                owner = "Analytics.Runtime",
                code,
                correlationId,
                context = failureContext,
                requiredAction,
            },
            SerializerOptions,
            context.RequestAborted);
    }
}
