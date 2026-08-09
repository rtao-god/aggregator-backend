using Aggregator.Catalog.Contracts;
using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Application;

/// <summary>Broker and producer identities retained with one Catalog eligibility event.</summary>
public sealed record PromotionEligibilityProjectionMessage(
    Guid MessageId,
    string ContractIdentity,
    string PayloadDigest,
    string CorrelationId,
    Guid? CausationId,
    CatalogListingPromotionEligibilityChanged Event);

/// <summary>Validated effect applied atomically to the Promotion inbox and local eligibility projection.</summary>
public sealed record PromotionEligibilityProjectionChange(
    Guid MessageId,
    string ContractIdentity,
    string PayloadDigest,
    string CorrelationId,
    Guid? CausationId,
    Guid? PublishedListingRevisionId,
    ListingPromotionEligibility Eligibility,
    string ProjectionDigest);

/// <summary>Result of an idempotent Promotion eligibility projection write.</summary>
public enum PromotionEligibilityProjectionApplyResult
{
    Applied = 1,
    Replayed = 2,
    Superseded = 3,
}

/// <summary>Atomic Promotion-owned inbox and Catalog eligibility projection boundary.</summary>
public interface IPromotionEligibilityProjectionStore
{
    public Task<PromotionEligibilityProjectionApplyResult> ApplyAsync(
        PromotionEligibilityProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken);
}

/// <summary>Owner context for reconciling placements against one current Catalog eligibility revision.</summary>
public sealed record PromotionEligibilityPlacementReconciliationRequest(
    ListingPromotionEligibility Eligibility,
    Guid SystemActorId,
    string CorrelationId,
    Guid CausationId,
    DateTimeOffset ChangedAtUtc);

/// <summary>Pauses active or scheduled placements that no longer satisfy the current Catalog eligibility facts.</summary>
public interface IPromotionEligibilityPlacementReconciler
{
    public Task<int> PauseIneligiblePlacementsAsync(
        PromotionEligibilityPlacementReconciliationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// Validates the producer-owned Catalog contract before Promotion persists any eligibility meaning.
/// </summary>
public sealed class ApplyCatalogListingPromotionEligibilityService(
    IPromotionEligibilityProjectionStore store,
    IPromotionClock clock)
{
    public async Task<PromotionEligibilityProjectionApplyResult> ApplyAsync(
        PromotionEligibilityProjectionMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(message.Event);
        ValidateEnvelope(message);
        var eligibility = ListingPromotionEligibility.Create(
            message.Event.CatalogKey,
            message.Event.ListingId,
            message.Event.IsPublished,
            message.Event.IsArchived,
            message.Event.HasBlockingDispute,
            message.Event.HasVerifiedContact,
            message.Event.VerifiedContactCapabilities,
            message.Event.CategoryKeys,
            message.Event.DistrictKey,
            message.Event.EligibilityRevision,
            message.Event.OccurredAtUtc);
        ValidateCanonicalProducerPayload(message.Event, eligibility);
        var projectionDigest = PromotionCanonicalJson.ComputeDigest(
            new EligibilityProjectionDigestDocument(
                eligibility.CatalogKey,
                eligibility.ListingId,
                message.Event.PublishedListingRevisionId,
                eligibility.IsPublished,
                eligibility.IsArchived,
                eligibility.HasBlockingDispute,
                eligibility.HasVerifiedContact,
                eligibility.ContactCapabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                eligibility.CategoryKeys.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                eligibility.DistrictKey,
                eligibility.SourceRevision,
                eligibility.ChangedAtUtc));
        var receivedAtUtc = clock.GetUtcNow();
        if (receivedAtUtc.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_CLOCK_NOT_UTC",
                500,
                "The Promotion eligibility projection clock returned a non-UTC timestamp.",
                "Correct the Promotion clock adapter before resuming the consumer.");
        }

        return await store.ApplyAsync(
            new PromotionEligibilityProjectionChange(
                message.MessageId,
                message.ContractIdentity,
                message.PayloadDigest,
                message.CorrelationId,
                message.CausationId,
                message.Event.PublishedListingRevisionId,
                eligibility,
                projectionDigest),
            receivedAtUtc,
            cancellationToken);
    }

    private static void ValidateEnvelope(PromotionEligibilityProjectionMessage message)
    {
        if (message.MessageId == Guid.Empty || message.Event.EventId == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_MESSAGE_ID_INVALID",
                422,
                "Catalog eligibility message and event identities must be non-empty UUIDs.",
                "Correct the producer envelope and replay the exact event.");
        }

        if (message.MessageId != message.Event.EventId)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_MESSAGE_ID_MISMATCH",
                422,
                "The broker message identity does not match the Catalog event identity.",
                "Block the message and inspect the producer outbox before replay.");
        }

        if (!string.Equals(
                message.ContractIdentity,
                CatalogIntegrationEventContracts.ListingPromotionEligibilityChanged,
                StringComparison.Ordinal))
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_CONTRACT_UNSUPPORTED",
                422,
                $"Catalog eligibility contract '{message.ContractIdentity}' is unsupported.",
                "Publish the supported producer-owned Catalog contract before replay.");
        }

        RequireDigest(message.PayloadDigest, "PROMOTION_ELIGIBILITY_PAYLOAD_DIGEST_INVALID");
        if (string.IsNullOrWhiteSpace(message.CorrelationId) ||
            message.CorrelationId.Length > 128 ||
            message.CorrelationId.Any(char.IsControl))
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_CORRELATION_INVALID",
                422,
                "Catalog eligibility correlation identity is invalid.",
                "Publish a printable correlation identity of at most 128 characters.");
        }

        if (message.CausationId == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_CAUSATION_INVALID",
                422,
                "Catalog eligibility causation identity cannot be an empty UUID.",
                "Publish either an absent causation identity or a non-empty UUID.");
        }
    }

    private static void ValidateCanonicalProducerPayload(
        CatalogListingPromotionEligibilityChanged integrationEvent,
        ListingPromotionEligibility eligibility)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent.VerifiedContactCapabilities);
        ArgumentNullException.ThrowIfNull(integrationEvent.CategoryKeys);
        if (integrationEvent.IsPublished != integrationEvent.PublishedListingRevisionId.HasValue ||
            integrationEvent.PublishedListingRevisionId == Guid.Empty)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_PUBLICATION_IDENTITY_INVALID",
                422,
                "Catalog eligibility publication state is not bound to one exact listing revision identity.",
                "Publish an exact revision identity only for a published listing.");
        }

        if (integrationEvent.HasVerifiedContact !=
            (integrationEvent.VerifiedContactCapabilities.Count > 0))
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_CONTACT_STATE_DIVERGED",
                422,
                "Catalog eligibility verified-contact state diverges from its capability set.",
                "Correct the Catalog event factory and replay the exact eligibility revision.");
        }

        if (!integrationEvent.IsPublished &&
            (integrationEvent.HasVerifiedContact ||
             integrationEvent.VerifiedContactCapabilities.Count > 0 ||
             integrationEvent.CategoryKeys.Count > 0 ||
             integrationEvent.DistrictKey is not null))
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_UNPUBLISHED_FACTS_PRESENT",
                422,
                "An unpublished listing eligibility event contains stale public scope or contact facts.",
                "Publish a fail-closed unpublished event without public eligibility facts.");
        }

        if (integrationEvent.IsPublished && integrationEvent.CategoryKeys.Count == 0)
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_CATEGORY_MISSING",
                422,
                "A published listing eligibility event must include at least one Catalog category.",
                "Correct the published listing revision or Catalog event factory before replay.");
        }

        RequireCanonicalSequence(
            integrationEvent.VerifiedContactCapabilities,
            eligibility.ContactCapabilities,
            "PROMOTION_ELIGIBILITY_CAPABILITIES_NOT_CANONICAL");
        RequireCanonicalSequence(
            integrationEvent.CategoryKeys,
            eligibility.CategoryKeys,
            "PROMOTION_ELIGIBILITY_CATEGORIES_NOT_CANONICAL");
        if (!string.Equals(
                integrationEvent.CatalogKey,
                eligibility.CatalogKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                integrationEvent.DistrictKey,
                eligibility.DistrictKey,
                StringComparison.Ordinal))
        {
            throw Failure(
                "PROMOTION_ELIGIBILITY_KEYS_NOT_CANONICAL",
                422,
                "Catalog eligibility keys are not in their canonical producer representation.",
                "Publish lowercase normalized Catalog keys without consumer-side repair.");
        }
    }

    private static void RequireCanonicalSequence(
        IReadOnlyList<string> producerValues,
        IReadOnlySet<string> normalizedValues,
        string code)
    {
        var canonical = normalizedValues
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (!producerValues.SequenceEqual(canonical, StringComparer.Ordinal))
        {
            throw Failure(
                code,
                422,
                "Catalog eligibility collection is not distinct and ordinally sorted in canonical form.",
                "Correct the Catalog event factory and replay the exact event bytes.");
        }
    }

    private static void RequireDigest(string digest, string code)
    {
        if (digest is not { Length: 64 } ||
            digest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw Failure(
                code,
                422,
                "Catalog eligibility payload digest is not a lowercase SHA-256 identity.",
                "Correct the producer envelope before replay.");
        }
    }

    private static PromotionApplicationException Failure(
        string code,
        int statusCode,
        string detail,
        string requiredAction) =>
        new(
            "Promotion.EligibilityProjection",
            code,
            statusCode,
            detail,
            requiredAction);

    private sealed record EligibilityProjectionDigestDocument(
        string CatalogKey,
        Guid ListingId,
        Guid? PublishedListingRevisionId,
        bool IsPublished,
        bool IsArchived,
        bool HasBlockingDispute,
        bool HasVerifiedContact,
        IReadOnlyList<string> ContactCapabilities,
        IReadOnlyList<string> CategoryKeys,
        string? DistrictKey,
        long SourceRevision,
        DateTimeOffset ChangedAtUtc);
}
