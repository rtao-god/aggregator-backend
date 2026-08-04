using System.Diagnostics;
using System.Text.Json;
using Aggregator.Promotion.Overlay.Application;

namespace Aggregator.Promotion.Overlay.Api;

public sealed class PromotionOverlayFailureMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<PromotionOverlayFailureMiddleware> _logger;

    public PromotionOverlayFailureMiddleware(
        RequestDelegate next,
        ILogger<PromotionOverlayFailureMiddleware> logger)
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
        catch (PromotionOverlayException exception) when (!context.Response.HasStarted)
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
                "PROMOTION_OVERLAY_UNHANDLED_FAILURE",
                StatusCodes.Status500InternalServerError,
                "Promotion overlay command failed before a typed owner result was produced.",
                "Inspect the correlated server diagnostic and correct the Promotion owner.",
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
            "Promotion overlay failure {Code} for correlation {CorrelationId}",
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
                type = $"https://errors.example/promotion-overlay/{code.Replace('_', '-')}",
                title = detail,
                status = statusCode,
                owner = "Promotion.Overlay",
                code,
                correlationId,
                context = failureContext,
                requiredAction,
            },
            SerializerOptions,
            context.RequestAborted);
    }
}
