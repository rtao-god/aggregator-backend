using Aggregator.Ingestion.Application;
using Aggregator.Ingestion.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Aggregator.Ingestion.Api;

[ApiController]
[Route("api/ingestion/producer-registrations")]
[Authorize(Policy = IngestionAuthorizationPolicies.ManageProducers)]
public sealed class IngestionProducerRegistrationsController(
    IngestionProducerRegistrationService service) : ControllerBase
{
    [HttpPut(Name = "PutIngestionProducerRegistration")]
    [ProducesResponseType<IngestionProducerRegistrationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionProducerRegistrationResponse>> PutAsync(
        [FromBody] PutIngestionProducerRegistrationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await service.PutAsync(
            new PutIngestionProducerRegistrationCommand(
                request.ProducerIdentity,
                request.ExpectedAggregateRevision,
                request.Active,
                request.SupportedContractRevisions,
                request.Reason,
                idempotencyKey,
                IngestionServiceIdentityAccessor.Require(HttpContext)),
            cancellationToken);
        return Ok(new IngestionProducerRegistrationResponse(
            IngestionProducerRegistrationService.ToDto(result.Registration),
            result.Replayed));
    }

    [HttpGet(Name = "GetIngestionProducerRegistration")]
    [ProducesResponseType<IngestionProducerRegistrationResponse>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IngestionProducerRegistrationResponse>> GetAsync(
        [FromQuery] string producerIdentity,
        CancellationToken cancellationToken)
    {
        var registration = await service.ReadAsync(producerIdentity, cancellationToken)
            ?? throw new IngestionApplicationException(
                "Ingestion.ProducerRegistry",
                "INGESTION_PRODUCER_NOT_FOUND",
                StatusCodes.Status404NotFound,
                $"Producer registration '{producerIdentity}' does not exist.",
                "Create the producer registration through the privileged Ingestion owner command.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["producerIdentity"] = producerIdentity,
                });
        return Ok(new IngestionProducerRegistrationResponse(
            IngestionProducerRegistrationService.ToDto(registration),
            Replayed: false));
    }
}
