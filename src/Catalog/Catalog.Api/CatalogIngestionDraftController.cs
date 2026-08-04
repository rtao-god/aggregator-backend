using System.Security.Claims;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Platform.ProblemDetails;

namespace Aggregator.Catalog.Api;

public static class CatalogIngestionAuthorizationPolicies
{
    public const string ExecuteDraftCommand = "catalog.ingestion";
}

public static class CatalogIngestionOperationIds
{
    public const string UpsertDraft = "CatalogIngestionUpsertDraft";
}

[ApiController]
[Route("api/catalog-command/ingestion")]
public sealed class CatalogIngestionDraftController(ICatalogIngestionDraftCommandHandler service) : ControllerBase
{
    [HttpPost("drafts", Name = CatalogIngestionOperationIds.UpsertDraft)]
    [Authorize(Policy = CatalogIngestionAuthorizationPolicies.ExecuteDraftCommand)]
    [EnableRateLimiting(CatalogRateLimitPolicies.Command)]
    [ProducesResponseType<CatalogIngestionCommandOutcome>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CatalogIngestionCommandOutcome>> UpsertAsync(
        [FromBody] CatalogIngestionUpsertDraftCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireIdempotencyIdentity(Request, command.CommandId);
        var outcome = await service.ExecuteAsync(
            command,
            RequireCallerIdentity(User),
            cancellationToken);
        return Ok(outcome);
    }

    private static void RequireIdempotencyIdentity(HttpRequest request, Guid commandId)
    {
        if (!request.Headers.TryGetValue("Idempotency-Key", out var values) ||
            values.Count != 1 ||
            !Guid.TryParse(values[0], out var parsed) ||
            parsed != commandId)
        {
            throw new CatalogIngestionDraftException(
                "Catalog.Commands",
                "CATALOG_INGESTION_IDEMPOTENCY_KEY_INVALID",
                StatusCodes.Status400BadRequest,
                "Idempotency-Key must equal the exact Catalog ingestion command ID.",
                "Replay the command with its canonical command ID as Idempotency-Key.");
        }
    }

    private static string RequireCallerIdentity(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 200)
        {
            throw new CatalogIngestionDraftException(
                "Catalog.Access",
                "CATALOG_INGESTION_CALLER_REQUIRED",
                StatusCodes.Status403Forbidden,
                "The authenticated token has no valid workload subject claim.",
                "Authenticate with the Catalog ingestion audience, scope and exact workload subject.");
        }

        return subject;
    }
}

internal sealed class CatalogIngestionFailureMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OwnerException)
        {
            throw;
        }
        catch (CatalogIngestionDraftException exception)
        {
            throw new OwnerException(
                new OwnerError(
                    exception.Owner,
                    exception.Code,
                    "Catalog ingestion command failed",
                    exception.StatusCode,
                    exception.Message,
                    exception.RequiredAction,
                    exception.Context),
                exception);
        }
    }
}
