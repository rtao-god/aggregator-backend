using System.Diagnostics;
using System.Text.Json;
using Aggregator.Analytics.Application;
using Microsoft.AspNetCore.Mvc;
using Platform.ProblemDetails;

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
        catch (OwnerException)
        {
            throw;
        }
        catch (AnalyticsCommandException exception) when (!context.Response.HasStarted)
        {
            await WriteAsync(
                context,
                exception.Owner,
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
                "Analytics.Transport",
                "ANALYTICS_UNHANDLED_FAILURE",
                StatusCodes.Status500InternalServerError,
                "Analytics request processing failed before a typed owner result was produced.",
                "Inspect the correlated server diagnostic and correct the responsible Analytics owner.",
                new Dictionary<string, object?>(StringComparer.Ordinal),
                exception);
        }
    }

    private async Task WriteAsync(
        HttpContext context,
        string owner,
        string code,
        int statusCode,
        string detail,
        string requiredAction,
        IReadOnlyDictionary<string, object?> failureContext,
        Exception exception)
    {
        var correlationAccessor = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
        var correlationId = correlationAccessor.CorrelationId
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.CreateVersion7().ToString("D");
        _logger.LogError(
            exception,
            "Analytics owner failure {Owner} {Code} for correlation {CorrelationId}",
            owner,
            code,
            correlationId);

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        var problem = new ProblemDetails
        {
            Type = $"https://errors.aggregator.local/analytics/{code}",
            Title = detail,
            Status = statusCode,
            Detail = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["owner"] = owner;
        problem.Extensions["code"] = code;
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["context"] = failureContext;
        problem.Extensions["requiredAction"] = requiredAction;
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            SerializerOptions,
            context.RequestAborted);
    }
}
