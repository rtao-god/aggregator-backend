using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>Persistence lifecycle of raw context attached to an accepted interaction event.</summary>
public enum InteractionEventRetentionState
{
    Raw = 1,
    Minimized = 2,
}

/// <summary>
/// Immutable persisted receipt used for semantic idempotency after raw interaction context may have been minimized.
/// </summary>
public sealed class InteractionEventReceipt
{
    private InteractionEventReceipt(
        Guid eventId,
        InteractionEventSemanticKey semanticKey,
        string payloadDigest,
        TrafficQualityState qualityState,
        DateTimeOffset receivedAtUtc,
        Guid publicReadRevisionId,
        Guid? listingId,
        InteractionEventRetentionState retentionState,
        DateTimeOffset? retainedAtUtc,
        Guid? retentionOperationId)
    {
        EventId = eventId;
        SemanticKey = semanticKey;
        PayloadDigest = payloadDigest;
        QualityState = qualityState;
        ReceivedAtUtc = receivedAtUtc;
        PublicReadRevisionId = publicReadRevisionId;
        ListingId = listingId;
        RetentionState = retentionState;
        RetainedAtUtc = retainedAtUtc;
        RetentionOperationId = retentionOperationId;
    }

    public Guid EventId { get; }

    public InteractionEventSemanticKey SemanticKey { get; }

    public string PayloadDigest { get; }

    public TrafficQualityState QualityState { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public Guid PublicReadRevisionId { get; }

    public Guid? ListingId { get; }

    public InteractionEventRetentionState RetentionState { get; }

    public DateTimeOffset? RetainedAtUtc { get; }

    public Guid? RetentionOperationId { get; }

    /// <summary>Creates the initial raw receipt for a newly accepted event.</summary>
    public static InteractionEventReceipt FromEvent(InteractionEvent interactionEvent)
    {
        ArgumentNullException.ThrowIfNull(interactionEvent);
        return Create(
            interactionEvent.Id,
            interactionEvent.SemanticKey,
            interactionEvent.PayloadDigest,
            interactionEvent.QualityState,
            interactionEvent.ReceivedAtUtc,
            interactionEvent.PublicReadRevisionId,
            interactionEvent.ListingId,
            InteractionEventRetentionState.Raw,
            retainedAtUtc: null,
            retentionOperationId: null);
    }

    /// <summary>Validates and creates a receipt rehydrated from canonical persistence evidence.</summary>
    public static InteractionEventReceipt Create(
        Guid eventId,
        InteractionEventSemanticKey semanticKey,
        string payloadDigest,
        TrafficQualityState qualityState,
        DateTimeOffset receivedAtUtc,
        Guid publicReadRevisionId,
        Guid? listingId,
        InteractionEventRetentionState retentionState,
        DateTimeOffset? retainedAtUtc,
        Guid? retentionOperationId)
    {
        AnalyticsDomainRules.RequireIdentifier(eventId, nameof(eventId));
        ArgumentNullException.ThrowIfNull(semanticKey);
        var validatedSemanticKey = InteractionEventSemanticKey.Create(
            semanticKey.ClientEventId,
            semanticKey.Kind);
        var normalizedDigest = AnalyticsDomainRules.RequireDigest(payloadDigest, nameof(payloadDigest));
        AnalyticsDomainRules.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        AnalyticsDomainRules.RequireIdentifier(publicReadRevisionId, nameof(publicReadRevisionId));

        if (!Enum.IsDefined(qualityState) ||
            qualityState is TrafficQualityState.Invalid or TrafficQualityState.Duplicate)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PERSISTED_QUALITY_STATE_INVALID",
                $"Persisted interaction quality state '{qualityState}' is not valid for an accepted event receipt.");
        }

        var listingRequired = validatedSemanticKey.Kind != InteractionEventKind.SearchResultsViewed;
        if (listingRequired && (listingId is null || listingId == Guid.Empty))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PERSISTED_LISTING_REQUIRED",
                $"Persisted interaction kind '{validatedSemanticKey.Kind}' requires a non-empty listing identity.");
        }

        if (!listingRequired && listingId is not null)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PERSISTED_LISTING_FORBIDDEN",
                "Persisted search-results interaction cannot carry a listing identity.");
        }

        if (!Enum.IsDefined(retentionState))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_RETENTION_STATE_INVALID",
                $"Persisted interaction retention state '{retentionState}' is unsupported.");
        }

        switch (retentionState)
        {
            case InteractionEventRetentionState.Raw when
                retainedAtUtc is not null || retentionOperationId is not null:
                throw new AnalyticsDomainException(
                    "ANALYTICS_RAW_RETENTION_EVIDENCE_FORBIDDEN",
                    "Raw interaction receipt cannot carry completed retention evidence.");
            case InteractionEventRetentionState.Minimized:
                if (retainedAtUtc is null || retentionOperationId is null || retentionOperationId == Guid.Empty)
                {
                    throw new AnalyticsDomainException(
                        "ANALYTICS_MINIMIZED_RETENTION_EVIDENCE_REQUIRED",
                        "Minimized interaction receipt requires exact retention time and operation identity.");
                }

                AnalyticsDomainRules.RequireUtc(retainedAtUtc.Value, nameof(retainedAtUtc));
                if (retainedAtUtc.Value < receivedAtUtc)
                {
                    throw new AnalyticsDomainException(
                        "ANALYTICS_RETENTION_TIME_INVALID",
                        "Interaction retention time cannot precede the server receive time.");
                }

                break;
        }

        return new InteractionEventReceipt(
            eventId,
            validatedSemanticKey,
            normalizedDigest,
            qualityState,
            receivedAtUtc,
            publicReadRevisionId,
            listingId,
            retentionState,
            retainedAtUtc,
            retentionOperationId);
    }
}
