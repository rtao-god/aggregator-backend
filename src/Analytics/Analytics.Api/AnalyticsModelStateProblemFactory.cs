using Microsoft.AspNetCore.Mvc;
using Platform.ProblemDetails;

namespace Aggregator.Analytics.Api;

internal static class AnalyticsModelStateProblemFactory
{
    public static IActionResult Create(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = context.ModelState
            .Where(item => item.Value?.Errors.Count > 0)
            .ToDictionary(
                item => item.Key,
                item => item.Value!.Errors.Select(error =>
                    string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? "The supplied JSON value is invalid."
                        : error.ErrorMessage).ToArray(),
                StringComparer.Ordinal);
        throw new OwnerException(new OwnerError(
            owner: "Analytics.Transport",
            code: "ANALYTICS_REQUEST_CONTRACT_INVALID",
            title: "Analytics request contract is invalid",
            status: StatusCodes.Status400BadRequest,
            detail: "The request cannot be bound to the active Analytics API contract.",
            requiredAction: "Correct the reported fields and use only declared string enum tokens.",
            context: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["errors"] = errors,
            }));
    }
}
