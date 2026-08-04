using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Platform.ProblemDetails;

namespace Aggregator.Analytics.Api;

internal static class AnalyticsAuthorizationStatusCodeWriter
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(StatusCodeContext statusCodeContext)
    {
        ArgumentNullException.ThrowIfNull(statusCodeContext);
        var context = statusCodeContext.HttpContext;
        if (context.Response.StatusCode is not StatusCodes.Status401Unauthorized and
            not StatusCodes.Status403Forbidden)
        {
            return;
        }

        var unauthenticated = context.Response.StatusCode == StatusCodes.Status401Unauthorized;
        var correlation = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
        var correlationId = correlation.CorrelationId
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.CreateVersion7().ToString("D");
        var problem = new ProblemDetails
        {
            Type = unauthenticated
                ? "https://errors.aggregator.local/analytics/access/authentication-required"
                : "https://errors.aggregator.local/analytics/access/authorization-denied",
            Title = unauthenticated ? "Authentication required" : "Authorization denied",
            Status = context.Response.StatusCode,
            Detail = unauthenticated
                ? "A valid Analytics API token is required."
                : "The authenticated identity lacks the required Analytics scope.",
            Instance = context.Request.Path,
        };
        problem.Extensions["owner"] = "Analytics.Access";
        problem.Extensions["code"] = unauthenticated
            ? "AUTHENTICATION_REQUIRED"
            : "AUTHORIZATION_DENIED";
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["requiredAction"] = unauthenticated
            ? "Authenticate with the Analytics API audience and retry."
            : "Request the exact OAuth scope required by this Analytics operation.";

        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            problem,
            SerializerOptions,
            context.RequestAborted);
    }
}
