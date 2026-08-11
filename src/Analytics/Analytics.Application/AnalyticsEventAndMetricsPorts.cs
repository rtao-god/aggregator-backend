using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

public enum InteractionEventRegistrationState
{
    Stored = 1,
    AlreadyApplied = 2,
    DigestConflict = 3,
}

/// <summary>Returns the exact persisted receipt selected by one semantic idempotency key.</summary>
public sealed record InteractionEventRegistrationResult(
    InteractionEventRegistrationState State,
    InteractionEventReceipt PersistedReceipt);

/// <summary>Persists accepted interaction events with atomic semantic idempotency.</summary>
public interface IAnalyticsEventStore
{
    public Task<InteractionEventReceipt?> GetAsync(
        InteractionEventSemanticKey semanticKey,
        CancellationToken cancellationToken);

    public Task<InteractionEventRegistrationResult> RegisterAsync(
        InteractionEvent interactionEvent,
        CancellationToken cancellationToken);
}

/// <summary>Validates an event against the Analytics-owned projection of public Query membership.</summary>
public interface IPublicReadReferenceStore
{
    public Task<PublicReadMembershipResult> ValidateInteractionAsync(
        Guid publicReadRevisionId,
        string catalogKey,
        Guid? listingId,
        PlacementContext placementContext,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Verifies the bounded public anti-abuse proof without persisting its raw token.</summary>
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

/// <summary>Authorizes owner metrics through the Analytics-local listing access projection.</summary>
public interface IListingMetricsAuthorizer
{
    public Task AuthorizeAsync(
        Guid actorId,
        Guid listingId,
        CancellationToken cancellationToken);
}
