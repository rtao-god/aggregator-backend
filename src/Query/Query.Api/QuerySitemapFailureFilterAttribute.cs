using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Aggregator.Query.Api;

/// <summary>Transport-only failure translation for the Query sitemap read boundary.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class QuerySitemapFailureFilterAttribute : ExceptionFilterAttribute
{
    public override void OnException(ExceptionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var problem = context.Exception switch
        {
            QueryDomainException exception => CreateProblem(
                StatusCodes.Status400BadRequest,
                "Query sitemap request is invalid.",
                exception.Message,
                "Query.Sitemap",
                exception.Code,
                "Correct the exact Catalog, locale or route value before retrying."),
            ArgumentOutOfRangeException exception => CreateProblem(
                StatusCodes.Status400BadRequest,
                "Query sitemap request is outside its bounded contract.",
                exception.Message,
                "Query.Sitemap",
                "QUERY_SITEMAP_REQUEST_OUT_OF_RANGE",
                "Use the documented sitemap page-size and cursor bounds."),
            ArgumentException exception => CreateProblem(
                StatusCodes.Status400BadRequest,
                "Query sitemap request is invalid.",
                exception.Message,
                "Query.Sitemap",
                "QUERY_SITEMAP_REQUEST_INVALID",
                "Discard the invalid cursor or parameter and restart from the first page."),
            QuerySitemapProjectionException exception => CreateProblem(
                StatusCodes.Status503ServiceUnavailable,
                "Query sitemap projection cannot be served safely.",
                exception.Message,
                exception.Owner,
                exception.Code,
                exception.RequiredAction),
            _ => null,
        };
        if (problem is null)
        {
            return;
        }

        context.Result = new ObjectResult(problem)
        {
            StatusCode = problem.Status,
        };
        context.ExceptionHandled = true;
    }

    private static ProblemDetails CreateProblem(
        int status,
        string title,
        string detail,
        string owner,
        string code,
        string requiredAction) =>
        new()
        {
            Status = status,
            Title = title,
            Detail = detail,
            Extensions =
            {
                ["owner"] = owner,
                ["code"] = code,
                ["requiredAction"] = requiredAction,
            },
        };
}
