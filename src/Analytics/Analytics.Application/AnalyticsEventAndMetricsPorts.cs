using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

public enum InteractionEventRegistrationState
{
    Stored = 1,
    AlreadyApplied = 2,
    DigestConflict = 3,
}

/// <summary>
/// Minimal immutable receipt retained for semantic idempotency after raw interaction context is minimized.
/// </summary>
public sealed record PersistedInteractionEventReceipt
{
    private PersistedInteractionEventReceipt(
        Guid eventId,
        InteractionEventSemanticKey semanticKey,
        string payloadDigest,
        TrafficQualityState qualityState,
        DateTimeOffset receivedAtUtc,
        Guid publicReadRevisionId,
        Guid? listingId)
    {
        EventId = eventId;
        SemanticKey = semanticKey;
        PayloadDigest = payloadDigest;
        QualityState = qualityState;
        ReceivedAtUtc = receivedAtUtc;
        PublicReadRevisionId = publicReadRevisionId;
        ListingId = listingId;
    }

    public Guid EventId { get; }

    public InteractionEventSemanticKey SemanticKey { get; }

    public string PayloadDigest { get; }

    public TrafficQualityState QualityState { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public Guid PublicReadRevisionId { get; }

    public Guid? ListingId { get; }

    public static PersistedInteractionEventReceipt FromDomain(InteractionEvent interactionEvent)
    {
        ArgumentNullException.ThrowIfNull(interactionEvent);
        return Create(
            interactionEvent.Id,
            interactionEvent.SemanticKey,
            interactionEvent.PayloadDigest,
            interactionEvent.QualityState,
            interactionEvent.ReceivedAtUtc,
            interactionEvent.PublicReadRevisionId,
            interactionEvent.ListingId);
    }

    public static PersistedInteractionEventReceipt Create(
        Guid eventId,
        InteractionEventSemanticKey semanticKey,
        string payloadDigest,
        TrafficQualityState qualityState,
        DateTimeOffset receivedAtUtc,
        Guid publicReadRevisionId,
        Guid? listingId)
    {
        AnalyticsDomainRules.RequireIdentifier(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(semanticKey);
        if (string.IsNullOrEmpty(payloadDigest) ||
            payloadDigest.Length != 64 ||
            payloadDigest.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'a' && character <= 'f'))))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_EVENT_RECEIPT_DIGEST_INVALID",
                "Persisted interaction receipt requires a lowercase SHA-256 payload digest.");
        }

        if (!Enum.IsDefined(qualityState))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_EVENT_RECEIPT_QUALITY_INVALID",
                $"Persisted interaction receipt quality state '{qualityState}' is unsupported.");
        }

        AnalyticsDomainRules.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        AnalyticsDomainRules.RequireIdentifier(publicReadRevisionId, nameof(publicReadRevisionId));
        if (listingId is { } actualListingId)
        {
            AnalyticsDomainRules.RequireIdentifier(actualListingId, nameof(listingId));
        }

        return new PersistedInteractionEventReceipt(
            eventId,
            semanticKey,
            payloadDigest,
            qualityState,
            receivedAtUtc,
            publicReadRevisionId,
            listingId);
    }
}

/// <summary>Returns the exact persisted receipt selected by one semantic idempotency key.</summary>
public sealed record InteractionEventRegistrationResult(
    InteractionEventRegistrationState State,
    PersistedInteractionEventReceipt Receipt);

/// <summary>Persists accepted interaction events with atomic semantic idempotency.</summary>
public interface IAnalyticsEventStore
{
    public Task<PersistedInteractionEventReceipt?> GetAsync(
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
