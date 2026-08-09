using Aggregator.Ingestion.Application;

namespace Aggregator.Ingestion.Infrastructure;

/// <summary>Consumer-facing view over the canonical revisioned producer-registration store.</summary>
public sealed class IngestionProducerRegistry(IIngestionProducerRegistrationStore store)
    : IIngestionProducerRegistry
{
    public async Task<RegisteredIngestionProducer?> GetAsync(
        string identity,
        CancellationToken cancellationToken)
    {
        var registration = await store.ReadAsync(identity, cancellationToken);
        return registration is null
            ? null
            : new RegisteredIngestionProducer(
                registration.ProducerIdentity,
                registration.Active,
                registration.SupportedContractRevisions);
    }
}
