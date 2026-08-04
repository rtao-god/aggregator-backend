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
    long ActivationRevision,
    DateTimeOffset ReceivedAtUtc);

public sealed record QueryProjectionActivation(
    QueryBaseProjection BaseProjection,
    QueryOverlayRevision PromotionOverlay,
    QueryOverlayRevision SafetyOverlay,
    PublicReadRevision PublicReadRevision);

public enum QueryProjectionActivationDisposition
{
    Activated = 1,
    Replayed = 2,
    IgnoredStale = 3,
}

public sealed record QueryProjectionActivationResult
{
    public QueryProjectionActivationResult(
        PublicReadRevision publicReadRevision,
        QueryProjectionActivationDisposition disposition)
    {
        PublicReadRevision = publicReadRevision ?? throw new ArgumentNullException(nameof(publicReadRevision));
        Disposition = disposition;
    }

    public QueryProjectionActivationResult(PublicReadRevision publicReadRevision, bool replayed)
        : this(
            publicReadRevision,
            replayed
                ? QueryProjectionActivationDisposition.Replayed
                : QueryProjectionActivationDisposition.Activated)
    {
    }

    public PublicReadRevision PublicReadRevision { get; }

    public QueryProjectionActivationDisposition Disposition { get; }

    public bool Replayed => Disposition == QueryProjectionActivationDisposition.Replayed;

    public bool IgnoredStale => Disposition == QueryProjectionActivationDisposition.IgnoredStale;
}

public interface IQueryProjectionStore
{
    public Task<QueryProjectionActivationResult> ActivateAsync(
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage,
        CancellationToken cancellationToken);
}
