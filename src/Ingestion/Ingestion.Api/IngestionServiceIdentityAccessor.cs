using System.Security.Claims;
using Aggregator.Ingestion.Application;

namespace Aggregator.Ingestion.Api;

internal static class IngestionServiceIdentityAccessor
{
    public static string Require(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
        {
            throw new IngestionApplicationException(
                "Ingestion.Access",
                "INGESTION_SERVICE_IDENTITY_REQUIRED",
                StatusCodes.Status403Forbidden,
                "The authenticated token has no valid service subject claim.",
                "Authenticate with a workload identity containing the exact OIDC subject.");
        }

        return subject;
    }
}
