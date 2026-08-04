using Microsoft.AspNetCore.Mvc;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

internal static class CatalogModelStateProblemFactory
{
    public static IActionResult Create(ActionContext actionContext)
    {
        ArgumentNullException.ThrowIfNull(actionContext);
        var correlation = actionContext.HttpContext.RequestServices
            .GetRequiredService<ICorrelationContextAccessor>();
        var problem = new ValidationProblemDetails(actionContext.ModelState)
        {
            Type = "https://errors.aggregator.local/catalog/transport/request-invalid",
            Title = "Catalog request is invalid",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The request could not be bound to the current Catalog wire contract.",
            Instance = actionContext.HttpContext.Request.Path,
        };
        problem.Extensions["owner"] = "Catalog.Transport";
        problem.Extensions["code"] = "CATALOG_REQUEST_INVALID";
        problem.Extensions["correlationId"] = correlation.CorrelationId;
        problem.Extensions["requiredAction"] =
            "Correct all reported wire-format errors and retry against the current generated contract.";
        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" },
        };
    }
}
