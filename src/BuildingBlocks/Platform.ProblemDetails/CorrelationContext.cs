using System.Diagnostics;
using Microsoft.AspNetCore.Http;

namespace Platform.ProblemDetails;

/// <summary>Provides the validated correlation identity for the current execution flow.</summary>
public interface ICorrelationContextAccessor
{
    string? CorrelationId { get; set; }
}

internal sealed class CorrelationContextAccessor : ICorrelationContextAccessor
{
    private static readonly AsyncLocal<Holder?> Current = new();

    public string? CorrelationId
    {
        get => Current.Value?.Value;
        set
        {
            if (Current.Value is not null)
            {
                Current.Value.Value = null;
            }

            if (value is not null)
            {
                Current.Value = new Holder { Value = value };
            }
        }
    }

    private sealed class Holder
    {
        public string? Value { get; set; }
    }
}

internal sealed class CorrelationMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
    }

    public async Task InvokeAsync(HttpContext context, ICorrelationContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(accessor);

        var correlationId = TryAccept(context.Request.Headers[HeaderName].ToString())
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.CreateVersion7().ToString("D");

        accessor.CorrelationId = correlationId;
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        try
        {
            await _next(context);
        }
        finally
        {
            accessor.CorrelationId = null;
        }
    }

    private static string? TryAccept(string candidate)
    {
        if (candidate.Length is < 8 or > 128)
        {
            return null;
        }

        foreach (var character in candidate)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':'))
            {
                return null;
            }
        }

        return candidate;
    }
}
