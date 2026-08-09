using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Application;

public interface IPromotionClock
{
    public DateTimeOffset GetUtcNow();
}

public interface IPromotionIdSource
{
    public Guid CreateId();
}

public sealed record PromotionActor(Guid Id)
{
    public static PromotionActor Create(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new PromotionApplicationException(
                "Promotion.Authorization",
                "PROMOTION_ACTOR_REQUIRED",
                401,
                "A non-empty Promotion actor identity is required.",
                "Authenticate through the configured OIDC provider before submitting this command.");
        }

        return new PromotionActor(id);
    }
}

public sealed record PromotionCommandIdentity(string Scope, string Key, string RequestDigest)
{
    public static PromotionCommandIdentity Create(string scope, string key, string requestDigest)
    {
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 150)
        {
            throw new PromotionApplicationException(
                "Promotion.Commands",
                "PROMOTION_IDEMPOTENCY_SCOPE_INVALID",
                500,
                "The Promotion command owner supplied an invalid idempotency scope.",
                "Correct the Promotion composition root before retrying.");
        }

        if (string.IsNullOrWhiteSpace(key) || key.Length > 200 || key.Any(char.IsControl))
        {
            throw new PromotionApplicationException(
                "Promotion.Commands",
                "PROMOTION_IDEMPOTENCY_KEY_INVALID",
                400,
                "A non-empty Idempotency-Key of at most 200 characters is required.",
                "Submit the command with one stable Idempotency-Key.");
        }

        if (requestDigest is not { Length: 64 } ||
            requestDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new PromotionApplicationException(
                "Promotion.Commands",
                "PROMOTION_REQUEST_DIGEST_INVALID",
                500,
                "The Promotion request digest is invalid.",
                "Correct canonical request hashing before retrying.");
        }

        return new PromotionCommandIdentity(scope.Trim(), key.Trim(), requestDigest);
    }
}

public sealed record PromotionCommandContext(
    PromotionActor Actor,
    string CorrelationId,
    Guid? CausationId)
{
    public static PromotionCommandContext Start(PromotionActor actor, string? correlationId = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var normalizedCorrelation = string.IsNullOrWhiteSpace(correlationId)
            ? Guid.CreateVersion7().ToString("D")
            : correlationId.Trim();
        if (normalizedCorrelation.Length > 128 || normalizedCorrelation.Any(char.IsControl))
        {
            throw new PromotionApplicationException(
                "Promotion.Commands",
                "PROMOTION_CORRELATION_INVALID",
                400,
                "Promotion correlation identity is invalid.",
                "Submit a printable correlation identity of at most 128 characters.");
        }

        return new PromotionCommandContext(actor, normalizedCorrelation, CausationId: null);
    }

    public static PromotionCommandContext Continue(
        PromotionActor actor,
        string correlationId,
        Guid causationId)
    {
        if (causationId == Guid.Empty)
        {
            throw new PromotionApplicationException(
                "Promotion.Commands",
                "PROMOTION_CAUSATION_INVALID",
                500,
                "Promotion continuation requires one non-empty causation identity.",
                "Propagate the exact producer message identity before creating Promotion effects.");
        }

        return Start(actor, correlationId) with { CausationId = causationId };
    }
}

public sealed record PromotionOutboxMessage(
    Guid Id,
    string EventType,
    string ContractIdentity,
    string Payload,
    string PayloadDigest,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    Guid? CausationId);

public sealed record PromotionCommandResult<TAggregate>(TAggregate Aggregate, bool Replayed)
    where TAggregate : class;

/// <summary>Persists Promotion aggregates, idempotent command results, eligibility reads and outbox effects.</summary>
public interface IPromotionRepository
{
    public Task<PromotionProduct?> GetProductAsync(Guid productId, CancellationToken cancellationToken);

    public Task<PromotionProduct?> GetProductByKeyAsync(string productKey, CancellationToken cancellationToken);

    public Task<PromotionEntitlement?> GetEntitlementAsync(
        Guid entitlementId,
        CancellationToken cancellationToken);

    public Task<SponsoredPlacement?> GetPlacementAsync(
        Guid placementId,
        CancellationToken cancellationToken);

    public Task<ListingPromotionEligibility?> GetEligibilityAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken);

    public Task<IReadOnlyList<PromotionEntitlement>> ListEntitlementsAsync(
        Guid listingId,
        CancellationToken cancellationToken);

    public Task<IReadOnlyList<SponsoredPlacement>> ListPlacementsAsync(
        string catalogKey,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken);

    public Task<bool> HasPlacementConflictAsync(
        SponsoredPlacement candidate,
        Guid? excludedPlacementId,
        CancellationToken cancellationToken);

    public Task<PromotionCommandResult<PromotionProduct>> AddProductAsync(
        PromotionProduct product,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        CancellationToken cancellationToken);

    public Task<PromotionCommandResult<PromotionProduct>> SaveProductAsync(
        PromotionProduct product,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        CancellationToken cancellationToken);

    public Task<PromotionCommandResult<PromotionEntitlement>> AddEntitlementAsync(
        PromotionEntitlement entitlement,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    public Task<PromotionCommandResult<PromotionEntitlement>> SaveEntitlementAsync(
        PromotionEntitlement entitlement,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    public Task<PromotionCommandResult<SponsoredPlacement>> AddPlacementAsync(
        SponsoredPlacement placement,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken);

    public Task<PromotionCommandResult<SponsoredPlacement>> SavePlacementAsync(
        SponsoredPlacement placement,
        long expectedStoredAggregateRevision,
        PromotionCommandIdentity commandIdentity,
        PromotionCommandContext commandContext,
        PromotionOutboxMessage outboxMessage,
        CancellationToken cancellationToken);
}
