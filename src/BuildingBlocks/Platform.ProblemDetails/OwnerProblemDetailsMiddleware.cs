using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Platform.ProblemDetails;

internal sealed class OwnerProblemDetailsMiddleware
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RequestDelegate _next;
    private readonly ILogger<OwnerProblemDetailsMiddleware> _logger;

    public OwnerProblemDetailsMiddleware(
        RequestDelegate next,
        ILogger<OwnerProblemDetailsMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context, ICorrelationContextAccessor correlation)
    {
        try
        {
            await _next(context);
        }
        catch (OwnerException exception) when (!context.Response.HasStarted)
        {
            await WriteOwnerFailureAsync(context, correlation.CorrelationId, exception);
        }
    }

    private async Task WriteOwnerFailureAsync(
        HttpContext context,
        string? correlationId,
        OwnerException exception)
    {
        var error = exception.Error;
        var effectiveCorrelationId = correlationId
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.CreateVersion7().ToString("D");

        _logger.LogWarning(
            exception,
            "Owner failure {Owner} {Code} for correlation {CorrelationId}",
            error.Owner,
            error.Code,
            effectiveCorrelationId);

        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails
        {
            Type = $"https://errors.aggregator.local/{error.Owner.ToLowerInvariant().Replace('.', '/')}/{error.Code.ToLowerInvariant().Replace('_', '-')}",
            Title = error.Title,
            Status = error.Status,
            Detail = error.Detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["owner"] = error.Owner;
        problem.Extensions["code"] = error.Code;
        problem.Extensions["correlationId"] = effectiveCorrelationId;
        problem.Extensions["context"] = error.Context;
        if (error.RequiredAction is not null)
        {
            problem.Extensions["requiredAction"] = error.RequiredAction;
        }

        context.Response.Clear();
        context.Response.StatusCode = error.Status;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(context.Response.Body, problem, SerializerOptions, context.RequestAborted);
    }
}
