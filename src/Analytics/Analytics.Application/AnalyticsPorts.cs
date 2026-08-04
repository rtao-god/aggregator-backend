using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

public enum PublicReadMembershipState
{
    Known = 1,
    UnknownRevision = 2,
    CatalogMismatch = 3,
    ListingNotPublic = 4,
    ListingRequired = 5,
}

public sealed record PublicReadMembershipResult(
    PublicReadMembershipState State,
    string? ActualCatalogKey,
    Guid? ActualListingId);

public interface IAnalyticsEventStore
{
    public Task<InteractionEvent?> GetAsync(
        InteractionEventSemanticKey semanticKey,
        CancellationToken cancellationToken);

    public Task AddAsync(InteractionEvent interactionEvent, CancellationToken cancellationToken);
}

public interface IPublicReadReferenceStore
{
    public Task<PublicReadMembershipResult> ValidateMembershipAsync(
        Guid publicReadRevisionId,
        string catalogKey,
        Guid? listingId,
        CancellationToken cancellationToken);
}

public interface IAntiAbuseVerifier
{
    public Task VerifyAsync(
        string antiAbuseToken,
        Guid clientEventId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

public interface IAnalyticsIdSource
{
    public Guid CreateId();
}

public interface IDailyListingMetricsStore
{
    public Task<IReadOnlyList<DailyListingMetrics>> GetRangeAsync(
        string catalogKey,
        Guid listingId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken);
}

public interface IListingMetricsAuthorizer
{
    public Task AuthorizeAsync(
        Guid actorId,
        Guid listingId,
        CancellationToken cancellationToken);
}
