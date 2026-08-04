using System.Diagnostics;
using System.Text.Json;
using Aggregator.Query.Application;

namespace Aggregator.Query.Api;

public sealed class QueryFailureMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<QueryFailureMiddleware> _logger;

    public QueryFailureMiddleware(
        RequestDelegate next,
        ILogger<QueryFailureMiddleware> logger)
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
        catch (QueryReadException exception) when (!context.Response.HasStarted)
        {
            await WriteFailureAsync(
                context,
                exception.Owner,
                exception.Code,
                exception.StatusCode,
                exception.Message,
                exception.RequiredAction,
                exception.Context,
                exception);
        }
        catch (QueryProjectionException exception) when (!context.Response.HasStarted)
        {
            await WriteFailureAsync(
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
            await WriteFailureAsync(
                context,
                "Query.Transport",
                "QUERY_UNHANDLED_FAILURE",
                StatusCodes.Status500InternalServerError,
                "Query request processing failed before a typed owner result was produced.",
                "Inspect the correlated server diagnostic and correct the responsible Query owner.",
                new Dictionary<string, object?>(StringComparer.Ordinal),
                exception);
        }
    }

    private async Task WriteFailureAsync(
        HttpContext context,
        string owner,
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
            "Query owner failure {Owner} {Code} for correlation {CorrelationId}",
            owner,
            code,
            correlationId);

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        var problem = new
        {
            type = $"https://errors.example/query/{code.ToLowerInvariant().Replace('_', '-')}",
            title = detail,
            status = statusCode,
            owner,
            code,
            correlationId,
            context = failureContext,
            requiredAction,
        };
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            SerializerOptions,
            context.RequestAborted);
    }
}
