using Aggregator.Analytics.Contracts;
using Aggregator.Analytics.Domain;

namespace Aggregator.Analytics.Application;

/// <summary>Accepts one public interaction against an exact public-read revision with semantic idempotency.</summary>
public sealed class SubmitInteractionEventService(
    IAnalyticsEventStore eventStore,
    IPublicReadReferenceStore publicReadReferences,
    IAntiAbuseVerifier antiAbuseVerifier,
    IAnalyticsIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<InteractionEventResponse> SubmitAsync(
        SubmitInteractionEventRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.PlacementContext);
        ArgumentNullException.ThrowIfNull(request.CampaignParameters);

        InteractionEventKind eventKind;
        InteractionEventSemanticKey semanticKey;
        PlacementContext placementContext;
        string requestDigest;
        try
        {
            eventKind = AnalyticsContractMapper.ToDomain(request.EventKind);
            semanticKey = InteractionEventSemanticKey.Create(request.ClientEventId, eventKind);
            placementContext = AnalyticsContractMapper.ToDomain(request.PlacementContext);
            requestDigest = AnalyticsCanonicalJson.ComputeRequestDigest(request);
        }
        catch (AnalyticsDomainException exception)
        {
            throw InvalidEvent(exception);
        }

        var existing = await eventStore.GetAsync(semanticKey, cancellationToken);
        if (existing is not null)
        {
            return ResolveExisting(existing, semanticKey, requestDigest);
        }

        if (string.IsNullOrWhiteSpace(request.AntiAbuseToken) || request.AntiAbuseToken.Length > 4096)
        {
            throw new AnalyticsCommandException(
                "Analytics.AntiAbuse",
                "ANALYTICS_ANTI_ABUSE_TOKEN_INVALID",
                400,
                "Interaction anti-abuse token is missing or exceeds the accepted size.",
                "Request a fresh bounded anti-abuse token and resubmit the same semantic event.");
        }

        var receivedAtUtc = timeProvider.GetUtcNow();
        InteractionEvent interactionEvent;
        try
        {
            interactionEvent = InteractionEvent.CreateAccepted(
                idSource.CreateId(),
                request.ClientEventId,
                eventKind,
                request.CatalogKey,
                request.ListingId,
                request.PublicReadRevisionId,
                request.OccurredAtUtc,
                receivedAtUtc,
                request.PageContext,
                placementContext,
                AnalyticsContractMapper.ToDomain(request.ReferrerClass),
                request.CampaignParameters,
                AnalyticsContractMapper.ToDomain(request.ConsentMode),
                requestDigest);
        }
        catch (AnalyticsDomainException exception)
        {
            throw InvalidEvent(exception);
        }

        await antiAbuseVerifier.VerifyAsync(
            request.AntiAbuseToken,
            request.ClientEventId,
            request.OccurredAtUtc,
            cancellationToken);
        var membership = await publicReadReferences.ValidateInteractionAsync(
            request.PublicReadRevisionId,
            interactionEvent.CatalogKey,
            interactionEvent.ListingId,
            interactionEvent.PlacementContext,
            interactionEvent.OccurredAtUtc,
            cancellationToken);
        EnsureKnownMembership(membership, interactionEvent);

        var registration = await eventStore.RegisterAsync(interactionEvent, cancellationToken);
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentNullException.ThrowIfNull(registration.Receipt);
        return registration.State switch
        {
            InteractionEventRegistrationState.Stored => ResolveStored(
                registration.Receipt,
                interactionEvent,
                InteractionAcceptanceStateContract.Accepted),
            InteractionEventRegistrationState.AlreadyApplied => ResolveStored(
                registration.Receipt,
                interactionEvent,
                InteractionAcceptanceStateContract.AlreadyApplied),
            InteractionEventRegistrationState.DigestConflict => throw DigestConflict(
                interactionEvent.SemanticKey,
                interactionEvent.PayloadDigest,
                registration.Receipt.PayloadDigest),
            _ => throw InvalidRegistrationResult(registration.State),
        };
    }

    private static InteractionEventResponse ResolveExisting(
        PersistedInteractionEventReceipt existing,
        InteractionEventSemanticKey expectedKey,
        string requestDigest)
    {
        EnsurePersistedIdentity(existing, expectedKey);
        if (!string.Equals(existing.PayloadDigest, requestDigest, StringComparison.Ordinal))
        {
            throw DigestConflict(expectedKey, requestDigest, existing.PayloadDigest);
        }

        return AnalyticsContractMapper.ToResponse(
            existing,
            InteractionAcceptanceStateContract.AlreadyApplied);
    }

    private static InteractionEventResponse ResolveStored(
        PersistedInteractionEventReceipt persisted,
        InteractionEvent requested,
        InteractionAcceptanceStateContract acceptanceState)
    {
        EnsurePersistedIdentity(persisted, requested.SemanticKey);
        if (!string.Equals(persisted.PayloadDigest, requested.PayloadDigest, StringComparison.Ordinal))
        {
            throw DigestConflict(
                requested.SemanticKey,
                requested.PayloadDigest,
                persisted.PayloadDigest);
        }

        return AnalyticsContractMapper.ToResponse(persisted, acceptanceState);
    }

    private static void EnsurePersistedIdentity(
        PersistedInteractionEventReceipt persisted,
        InteractionEventSemanticKey expectedKey)
    {
        if (persisted.SemanticKey != expectedKey)
        {
            throw new AnalyticsCommandException(
                "Analytics.Persistence",
                "ANALYTICS_EVENT_STORE_IDENTITY_CORRUPT",
                500,
                "Analytics event store returned an event for a different semantic identity.",
                "Stop interaction intake and repair the Analytics event-store identity invariant.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["expectedClientEventId"] = expectedKey.ClientEventId,
                    ["expectedEventKind"] = expectedKey.Kind.ToString(),
                    ["actualClientEventId"] = persisted.SemanticKey.ClientEventId,
                    ["actualEventKind"] = persisted.SemanticKey.Kind.ToString(),
                });
        }
    }

    private static void EnsureKnownMembership(
        PublicReadMembershipResult membership,
        InteractionEvent interactionEvent)
    {
        ArgumentNullException.ThrowIfNull(membership);
        switch (membership.State)
        {
            case PublicReadMembershipState.Known:
                return;
            case PublicReadMembershipState.UnknownRevision:
                throw MembershipFailure(
                    "ANALYTICS_PUBLIC_READ_REVISION_UNKNOWN",
                    "The supplied public-read revision is not known to Analytics.",
                    "Replay the exact Query public-read activation before accepting interactions.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.CatalogMismatch:
                throw MembershipFailure(
                    "ANALYTICS_PUBLIC_READ_CATALOG_MISMATCH",
                    "The supplied public-read revision belongs to another catalog.",
                    "Send the catalog identity published with the exact public-read revision.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.ListingRequired:
                throw MembershipFailure(
                    "ANALYTICS_PUBLIC_READ_LISTING_REQUIRED",
                    "The interaction kind or sponsored context requires a listing identity.",
                    "Send the exact public listing identity from the active Query response.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.ListingNotPublic:
                throw MembershipFailure(
                    "ANALYTICS_PUBLIC_READ_LISTING_UNKNOWN",
                    "The supplied listing is not public in the exact public-read revision.",
                    "Use a listing identity included in the selected Query public-read revision.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementNotPublic:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_UNKNOWN",
                    "The supplied sponsored placement is not public in the exact public-read revision.",
                    "Use the exact sponsored placement identity included in the Query response.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementListingMismatch:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_LISTING_MISMATCH",
                    "The sponsored placement belongs to another listing.",
                    "Send the listing identity bound to the exact sponsored placement.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementScopeMismatch:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_SCOPE_MISMATCH",
                    "The sponsored placement scope does not match the Query projection.",
                    "Send the exact sponsored scope key from the selected Query response.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementInactive:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_INACTIVE",
                    "The sponsored placement was not active at the interaction occurrence time.",
                    "Submit only interactions attributed to the Query-owned placement interval.",
                    membership,
                    interactionEvent);
            default:
                throw new AnalyticsCommandException(
                    "Analytics.PublicReference",
                    "ANALYTICS_PUBLIC_READ_MEMBERSHIP_STATE_INVALID",
                    500,
                    $"Analytics public-read membership returned unsupported state '{membership.State}'.",
                    "Stop interaction intake and align the public-read projection result contract.");
        }
    }

    private static AnalyticsCommandException MembershipFailure(
        string code,
        string detail,
        string requiredAction,
        PublicReadMembershipResult membership,
        InteractionEvent interactionEvent) =>
        new(
            "Analytics.PublicReference",
            code,
            422,
            detail,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["publicReadRevisionId"] = interactionEvent.PublicReadRevisionId,
                ["catalogKey"] = interactionEvent.CatalogKey,
                ["listingId"] = interactionEvent.ListingId,
                ["placementId"] = interactionEvent.PlacementContext.PlacementId,
                ["requestedPlacementScopeKey"] = interactionEvent.PlacementContext.ScopeKey,
                ["actualCatalogKey"] = membership.ActualCatalogKey,
                ["actualListingId"] = membership.ActualListingId,
                ["actualPlacementId"] = membership.ActualPlacementId,
                ["actualPlacementListingId"] = membership.ActualPlacementListingId,
                ["actualPlacementScopeKey"] = membership.ActualPlacementScopeKey,
            });

    private static AnalyticsCommandException DigestConflict(
        InteractionEventSemanticKey semanticKey,
        string requestedDigest,
        string existingDigest) =>
        new(
            "Analytics.Events",
            "ANALYTICS_EVENT_IDEMPOTENCY_CONFLICT",
            409,
            "The interaction semantic key already exists with a different canonical payload digest.",
            "Reuse the exact original payload or submit a new client event identity.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["clientEventId"] = semanticKey.ClientEventId,
                ["eventKind"] = semanticKey.Kind.ToString(),
                ["requestedDigest"] = requestedDigest,
                ["existingDigest"] = existingDigest,
            });

    private static AnalyticsCommandException InvalidEvent(AnalyticsDomainException exception) =>
        new(
            "Analytics.Events",
            exception.Code,
            422,
            exception.Message,
            "Correct the interaction payload and submit it under the same semantic identity only when the canonical payload is unchanged.",
            innerException: exception);

    private static AnalyticsCommandException InvalidRegistrationResult(
        InteractionEventRegistrationState state) =>
        new(
            "Analytics.Persistence",
            "ANALYTICS_EVENT_REGISTRATION_STATE_INVALID",
            500,
            $"Analytics event store returned unsupported registration state '{state}'.",
            "Stop interaction intake and repair the event-store result mapping.");
}
