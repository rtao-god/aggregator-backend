using Aggregator.Catalog.Contracts;
using Aggregator.Query.Domain;

namespace Aggregator.Query.Application;

public interface IQueryClock
{
    public DateTimeOffset GetUtcNow();
}

public interface IQueryIdFactory
{
    public Guid Create();
}

public interface ICatalogPublicationArtifactReader
{
    public Task<CatalogPublicationArtifact> ReadAsync(
        string objectKey,
        string expectedDigest,
        CancellationToken cancellationToken);
}

public sealed record QueryInboxMessage(
    Guid EventId,
    string EventType,
    string PayloadDigest,
    long PublicationSequence,
    DateTimeOffset ReceivedAtUtc);

public sealed record QueryProjectionActivation(
    QueryBaseProjection BaseProjection,
    QueryOverlayRevision PromotionOverlay,
    QueryOverlayRevision SafetyOverlay,
    PublicReadRevision PublicReadRevision);

public sealed record QueryProjectionActivationResult(
    PublicReadRevision PublicReadRevision,
    bool Replayed);

public interface IQueryProjectionStore
{
    public Task<QueryProjectionActivationResult> ActivateAsync(
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage,
        CancellationToken cancellationToken);
}
