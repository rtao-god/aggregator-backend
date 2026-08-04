using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Platform.ProblemDetails;

namespace Aggregator.Ingestion.Api;

internal static class IngestionModelStateProblemFactory
{
    public static IActionResult Create(ActionContext actionContext)
    {
        ArgumentNullException.ThrowIfNull(actionContext);
        var context = actionContext.HttpContext;
        var correlation = context.RequestServices.GetRequiredService<ICorrelationContextAccessor>();
        var correlationId = correlation.CorrelationId
            ?? Activity.Current?.TraceId.ToString()
            ?? Guid.CreateVersion7().ToString("D");
        var problem = new ValidationProblemDetails(actionContext.ModelState)
        {
            Type = "https://errors.aggregator.local/ingestion/contracts/request-invalid",
            Title = "Ingestion request contract invalid",
            Status = StatusCodes.Status400BadRequest,
            Detail = "The HTTP payload does not satisfy the declared Ingestion transport contract.",
            Instance = context.Request.Path,
        };
        problem.Extensions["owner"] = "Ingestion.Contracts";
        problem.Extensions["code"] = "INGESTION_REQUEST_INVALID";
        problem.Extensions["correlationId"] = correlationId;
        problem.Extensions["requiredAction"] =
            "Correct the request to match the generated Ingestion client contract.";
        return new BadRequestObjectResult(problem)
        {
            ContentTypes = { "application/problem+json" },
        };
    }
}
