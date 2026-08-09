using Aggregator.Catalog.Contracts;

namespace Aggregator.Query.Application;

public sealed class QueryProjectionService(
    ICatalogPublicationArtifactReader artifactReader,
    IQueryActivationCheckpointReader checkpointReader,
    IQueryProjectionStore projectionStore,
    IQueryClock clock,
    IQueryIdFactory idFactory)
{
    public Task<QueryProjectionActivationResult> ApplyPublicationAsync(
        CatalogPublicationActivated activation,
        string eventPayloadDigest,
        CancellationToken cancellationToken) =>
        ApplyPublicationAsync(
            activation,
            eventPayloadDigest,
            activation?.EventId.ToString("D")
                ?? throw new ArgumentNullException(nameof(activation)),
            cancellationToken);

    public async Task<QueryProjectionActivationResult> ApplyPublicationAsync(
        CatalogPublicationActivated activation,
        string eventPayloadDigest,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ValidateActivation(activation);
        if (string.IsNullOrWhiteSpace(eventPayloadDigest) ||
            eventPayloadDigest.Length != 64 ||
            eventPayloadDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new QueryProjectionException(
                "Query.Inbox",
                "QUERY_EVENT_DIGEST_INVALID",
                422,
                "Catalog publication event payload digest is invalid.",
                "Reject the broker message and inspect the producer outbox payload.");
        }

        var lastActivationRevision = await checkpointReader.GetLastActivationRevisionAsync(
            activation.CatalogKey,
            cancellationToken);
        QueryActivationRevisionGuard.EnsureCanApply(
            activation.CatalogKey,
            activation.ActivationRevision,
            lastActivationRevision);

        var artifact = await artifactReader.ReadAsync(
            activation.ArtifactKey,
            activation.ArtifactDigest,
            cancellationToken);
        var builtAtUtc = clock.GetUtcNow();
        var projection = CatalogPublicationProjectionBuilder.Build(
            activation,
            artifact,
            idFactory.Create(),
            idFactory.Create(),
            idFactory.Create(),
            idFactory.Create(),
            builtAtUtc);
        var inbox = new QueryInboxMessage(
            activation.EventId,
            CatalogIntegrationEventTypes.PublicationActivated,
            eventPayloadDigest,
            activation.ActivationRevision,
            builtAtUtc)
        {
            CorrelationId = NormalizeCorrelationId(correlationId),
        };
        return await projectionStore.ActivateAsync(projection, inbox, cancellationToken);
    }

    private static string NormalizeCorrelationId(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw new QueryProjectionException(
                "Query.Inbox",
                "QUERY_CORRELATION_ID_INVALID",
                422,
                "Catalog publication event correlation ID is missing or too long.",
                "Republish the Catalog event with a bounded correlation identity.");
        }

        return correlationId.Trim();
    }

    private static void ValidateActivation(CatalogPublicationActivated activation)
    {
        if (activation.EventId == Guid.Empty || activation.PublicationId == Guid.Empty || activation.ConfigurationRevisionId == Guid.Empty)
        {
            throw new QueryProjectionException(
                "Query.Inbox",
                "QUERY_EVENT_IDENTITY_INVALID",
                422,
                "Catalog publication event contains an empty required identity.",
                "Correct the Catalog producer event before replaying it.");
        }

        if (string.IsNullOrWhiteSpace(activation.CatalogKey) ||
            string.IsNullOrWhiteSpace(activation.ArtifactKey) ||
            activation.PublicationSequence <= 0 ||
            activation.ActivationRevision <= 0)
        {
            throw new QueryProjectionException(
                "Query.Inbox",
                "QUERY_EVENT_CONTRACT_INVALID",
                422,
                "Catalog publication event violates the Query ingestion contract.",
                "Correct the Catalog producer event before replaying it.");
        }

        if (activation.OccurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new QueryProjectionException(
                "Query.Inbox",
                "QUERY_EVENT_TIMESTAMP_NOT_UTC",
                422,
                "Catalog publication event timestamp is not UTC.",
                "Correct the Catalog producer event timestamp before replaying it.");
        }
    }
}
