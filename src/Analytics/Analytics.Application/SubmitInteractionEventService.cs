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
        ArgumentNullException.ThrowIfNull(registration.PersistedReceipt);
        return registration.State switch
        {
            InteractionEventRegistrationState.Stored => ResolveStored(
                registration.PersistedReceipt,
                interactionEvent,
                InteractionAcceptanceStateContract.Accepted),
            InteractionEventRegistrationState.AlreadyApplied => ResolveStored(
                registration.PersistedReceipt,
                interactionEvent,
                InteractionAcceptanceStateContract.AlreadyApplied),
            InteractionEventRegistrationState.DigestConflict => throw DigestConflict(
                interactionEvent.SemanticKey,
                interactionEvent.PayloadDigest,
                registration.PersistedReceipt.PayloadDigest),
            _ => throw InvalidRegistrationResult(registration.State),
        };
    }

    private static InteractionEventResponse ResolveExisting(
        InteractionEventReceipt existing,
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
        InteractionEventReceipt persisted,
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
        InteractionEventReceipt persisted,
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
                    "The supplied public-read revision is unknown to Analytics.",
                    "Wait for the Analytics public-reference projection to consume the exact Query activation, then retry.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.CatalogMismatch:
                throw MembershipFailure(
                    "ANALYTICS_PUBLIC_READ_CATALOG_MISMATCH",
                    "The supplied public-read revision belongs to another catalog.",
                    "Send the catalog identity published with the exact public-read revision.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.ListingNotPublic:
                throw MembershipFailure(
                    "ANALYTICS_PUBLIC_LISTING_UNKNOWN",
                    "The supplied listing is not public in the exact public-read revision.",
                    "Send an interaction only for a listing present after the revision's safety suppression.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.ListingRequired:
                throw MembershipFailure(
                    "ANALYTICS_PUBLIC_LISTING_REQUIRED",
                    "The interaction kind requires a listing reference in the public-read revision.",
                    "Send the exact public listing identity associated with the interaction.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementNotPublic:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_UNKNOWN",
                    "The supplied sponsored placement is not present in the exact public-read revision.",
                    "Send the Query-owned placement identity published with the exact public-read revision.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementListingMismatch:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_LISTING_MISMATCH",
                    "The supplied sponsored placement belongs to another public listing.",
                    "Send the listing identity bound to the exact Query sponsored placement reference.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementScopeMismatch:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_SCOPE_MISMATCH",
                    "The supplied sponsored placement scope does not match the exact Query reference.",
                    "Send the scope key published for the exact sponsored placement.",
                    membership,
                    interactionEvent);
            case PublicReadMembershipState.SponsoredPlacementInactive:
                throw MembershipFailure(
                    "ANALYTICS_SPONSORED_PLACEMENT_INACTIVE",
                    "The sponsored placement was not active at the interaction occurrence time.",
                    "Do not attribute interactions outside the Query-owned placement interval.",
                    membership,
                    interactionEvent);
            default:
                throw new AnalyticsCommandException(
                    "Analytics.PublicReference",
                    "ANALYTICS_PUBLIC_REFERENCE_RESULT_INVALID",
                    500,
                    $"Public-reference store returned unsupported state '{membership.State}'.",
                    "Stop interaction intake and repair the Analytics public-reference projection adapter.");
        }
    }

    private static AnalyticsCommandException MembershipFailure(
        string code,
        string message,
        string requiredAction,
        PublicReadMembershipResult membership,
        InteractionEvent interactionEvent) =>
        new(
            "Analytics.PublicReference",
            code,
            422,
            message,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["publicReadRevisionId"] = interactionEvent.PublicReadRevisionId,
                ["requestedCatalogKey"] = interactionEvent.CatalogKey,
                ["requestedListingId"] = interactionEvent.ListingId,
                ["actualCatalogKey"] = membership.ActualCatalogKey,
                ["actualListingId"] = membership.ActualListingId,
                ["requestedPlacementId"] = interactionEvent.PlacementContext.PlacementId,
                ["requestedPlacementScopeKey"] = interactionEvent.PlacementContext.ScopeKey,
                ["actualPlacementId"] = membership.ActualPlacementId,
                ["actualPlacementListingId"] = membership.ActualPlacementListingId,
                ["actualPlacementScopeKey"] = membership.ActualPlacementScopeKey,
            });

    private static AnalyticsCommandException DigestConflict(
        InteractionEventSemanticKey semanticKey,
        string requestDigest,
        string persistedDigest) =>
        new(
            "Analytics.Events",
            "ANALYTICS_EVENT_IDEMPOTENCY_CONFLICT",
            409,
            "The semantic interaction identity already exists with another payload digest.",
            "Reuse the original payload or submit a new client event identity.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["clientEventId"] = semanticKey.ClientEventId,
                ["eventKind"] = semanticKey.Kind.ToString(),
                ["requestDigest"] = requestDigest,
                ["persistedDigest"] = persistedDigest,
            });

    private static AnalyticsCommandException InvalidEvent(AnalyticsDomainException exception) =>
        new(
            "Analytics.Events",
            exception.Code,
            422,
            exception.Message,
            "Correct the interaction event to satisfy the Analytics owner contract.");

    private static AnalyticsCommandException InvalidRegistrationResult(
        InteractionEventRegistrationState state) =>
        new(
            "Analytics.Persistence",
            "ANALYTICS_EVENT_REGISTRATION_RESULT_INVALID",
            500,
            $"Analytics event store returned unsupported registration state '{state}'.",
            "Stop interaction intake and repair the Analytics event-store adapter.");
}
