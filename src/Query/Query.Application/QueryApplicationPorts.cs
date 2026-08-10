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

/// <summary>Reads the last durable Catalog activation revision accepted by Query.</summary>
public interface IQueryActivationCheckpointReader
{
    public Task<long?> GetLastActivationRevisionAsync(
        string catalogKey,
        CancellationToken cancellationToken);
}

public sealed record QueryInboxMessage(
    Guid EventId,
    string EventType,
    string PayloadDigest,
    long ActivationRevision,
    DateTimeOffset ReceivedAtUtc)
{
    /// <summary>Correlation chain preserved from the producer message or started by a direct owner call.</summary>
    public string CorrelationId { get; init; } = EventId.ToString("D");
}

public sealed record QueryProjectionActivation(
    QueryBaseProjection BaseProjection,
    QueryOverlayRevision PromotionOverlay,
    QueryOverlayRevision SafetyOverlay,
    PublicReadRevision PublicReadRevision,
    PublicSitemapProjectionArtifact SeoProjection)
{
    public QueryProjectionActivation(
        QueryBaseProjection baseProjection,
        QueryOverlayRevision promotionOverlay,
        QueryOverlayRevision safetyOverlay,
        PublicReadRevision publicReadRevision)
        : this(
            baseProjection,
            promotionOverlay,
            safetyOverlay,
            publicReadRevision,
            CreateEmptySeoProjection(publicReadRevision))
    {
    }

    private static PublicSitemapProjectionArtifact CreateEmptySeoProjection(
        PublicReadRevision publicReadRevision)
    {
        ArgumentNullException.ThrowIfNull(publicReadRevision);
        return PublicSitemapProjectionArtifactBuilder.Build(
            publicReadRevision.Id,
            expectedCurrentPublicReadRevisionId: null,
            publicReadRevision.CatalogKey,
            Array.Empty<QuerySitemapDocument>(),
            Array.Empty<QueryRouteRedirectDocument>(),
            publicReadRevision.CreatedAtUtc);
    }
}

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
