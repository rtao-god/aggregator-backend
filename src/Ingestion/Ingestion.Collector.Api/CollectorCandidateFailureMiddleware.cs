using System.Diagnostics;
using System.Text.Json;
using Aggregator.Ingestion.Collector.Application;

namespace Aggregator.Ingestion.Collector.Api;

public sealed class CollectorCandidateFailureMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<CollectorCandidateFailureMiddleware> _logger;

    public CollectorCandidateFailureMiddleware(
        RequestDelegate next,
        ILogger<CollectorCandidateFailureMiddleware> logger)
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
        catch (CollectorCandidateException exception) when (!context.Response.HasStarted)
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
                "COLLECTOR_UNHANDLED_FAILURE",
                StatusCodes.Status500InternalServerError,
                "Collector candidate intake failed before a typed owner result was produced.",
                "Inspect the correlated diagnostic and correct the Ingestion collector owner.",
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
            "Collector candidate failure {Code} for correlation {CorrelationId}",
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
                type = $"https://errors.example/ingestion-collector/{code.Replace('_', '-')}",
                title = detail,
                status = statusCode,
                owner = "Ingestion.Collector",
                code,
                correlationId,
                context = failureContext,
                requiredAction,
            },
            SerializerOptions,
            context.RequestAborted);
    }
}
