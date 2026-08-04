using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Application;

public sealed class PromotionEntitlementService(
    IPromotionRepository repository,
    IPromotionIdSource idSource,
    IPromotionClock clock)
{
    public async Task<PromotionResponseResult<PromotionEntitlementResponse>> GrantAsync(
        GrantPromotionEntitlementRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        var product = await repository.GetProductByKeyAsync(request.ProductKey, cancellationToken)
            ?? throw new PromotionApplicationException(
                "Promotion.Products",
                "PROMOTION_PRODUCT_NOT_FOUND",
                404,
                $"Promotion product '{request.ProductKey}' was not found.",
                "Create and activate the exact Promotion product before granting an entitlement.");
        if (product.State != PromotionProductState.Active)
        {
            throw new PromotionApplicationException(
                "Promotion.Entitlements",
                "PROMOTION_PRODUCT_NOT_ACTIVE",
                422,
                $"Promotion product '{product.Key}' is not active.",
                "Activate the product or select another active product.");
        }

        var window = PromotionWindow.Create(request.StartsAtUtc, request.EndsAtUtc);
        var sourceType = PromotionContractMapper.ToDomain(request.SourceType);
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = "promotion-entitlement-grant",
            request.ListingId,
            productKey = product.Key,
            sourceType,
            request.ExternalReference,
            request.StartsAtUtc,
            request.EndsAtUtc,
            request.AuditReason,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.listing.{request.ListingId:N}.entitlement.grant",
            idempotencyKey,
            requestDigest);
        var occurredAtUtc = clock.GetUtcNow();
        var entitlement = PromotionEntitlement.Grant(
            idSource.CreateId(),
            request.ListingId,
            product.Key,
            sourceType,
            request.ExternalReference,
            window,
            commandContext.Actor.Id,
            request.AuditReason,
            occurredAtUtc);
        var eventId = idSource.CreateId();
        var integrationEvent = PromotionContractMapper.ToEvent(entitlement, eventId, occurredAtUtc);
        var outbox = PromotionOutboxMessageFactory.Create(
            eventId,
            PromotionIntegrationEventTypes.EntitlementChanged,
            PromotionIntegrationEventContracts.EntitlementChanged,
            integrationEvent,
            occurredAtUtc,
            commandContext);
        var result = await repository.AddEntitlementAsync(
            entitlement,
            commandIdentity,
            commandContext,
            outbox,
            cancellationToken);
        return new PromotionResponseResult<PromotionEntitlementResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    public Task<PromotionResponseResult<PromotionEntitlementResponse>> PauseAsync(
        Guid entitlementId,
        ChangePromotionEntitlementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ChangeStateAsync(
            entitlementId,
            request,
            commandContext,
            idempotencyKey,
            "pause",
            static (entitlement, expectedRevision, actorId, reason, changedAtUtc) =>
                entitlement.Pause(expectedRevision, actorId, reason, changedAtUtc),
            cancellationToken);

    public Task<PromotionResponseResult<PromotionEntitlementResponse>> ResumeAsync(
        Guid entitlementId,
        ChangePromotionEntitlementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ChangeStateAsync(
            entitlementId,
            request,
            commandContext,
            idempotencyKey,
            "resume",
            static (entitlement, expectedRevision, actorId, reason, changedAtUtc) =>
                entitlement.Resume(expectedRevision, actorId, reason, changedAtUtc),
            cancellationToken);

    public Task<PromotionResponseResult<PromotionEntitlementResponse>> RevokeAsync(
        Guid entitlementId,
        ChangePromotionEntitlementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ChangeStateAsync(
            entitlementId,
            request,
            commandContext,
            idempotencyKey,
            "revoke",
            static (entitlement, expectedRevision, actorId, reason, changedAtUtc) =>
                entitlement.Revoke(expectedRevision, actorId, reason, changedAtUtc),
            cancellationToken);

    public async Task<PromotionEntitlementResponse> GetAsync(
        Guid entitlementId,
        CancellationToken cancellationToken) =>
        PromotionContractMapper.ToResponse(await RequireEntitlementAsync(entitlementId, cancellationToken));

    public async Task<IReadOnlyList<PromotionEntitlementResponse>> ListForListingAsync(
        Guid listingId,
        CancellationToken cancellationToken)
    {
        if (listingId == Guid.Empty)
        {
            throw new PromotionApplicationException(
                "Promotion.Entitlements",
                "PROMOTION_LISTING_ID_INVALID",
                400,
                "Listing ID is empty.",
                "Use the exact listing ID from the Catalog contract.");
        }

        var entitlements = await repository.ListEntitlementsAsync(listingId, cancellationToken);
        return entitlements
            .OrderBy(item => item.EffectiveWindow.StartsAtUtc)
            .ThenBy(item => item.Id)
            .Select(PromotionContractMapper.ToResponse)
            .ToArray();
    }

    private async Task<PromotionResponseResult<PromotionEntitlementResponse>> ChangeStateAsync(
        Guid entitlementId,
        ChangePromotionEntitlementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        string operation,
        Action<PromotionEntitlement, long, Guid, string, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(transition);
        var entitlement = await RequireEntitlementAsync(entitlementId, cancellationToken);
        var storedRevision = entitlement.AggregateRevision;
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = $"promotion-entitlement-{operation}",
            entitlementId,
            request.ExpectedAggregateRevision,
            request.AuditReason,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.entitlement.{entitlementId:N}.{operation}",
            idempotencyKey,
            requestDigest);
        var occurredAtUtc = clock.GetUtcNow();
        transition(
            entitlement,
            request.ExpectedAggregateRevision,
            commandContext.Actor.Id,
            request.AuditReason,
            occurredAtUtc);
        var eventId = idSource.CreateId();
        var integrationEvent = PromotionContractMapper.ToEvent(entitlement, eventId, occurredAtUtc);
        var outbox = PromotionOutboxMessageFactory.Create(
            eventId,
            PromotionIntegrationEventTypes.EntitlementChanged,
            PromotionIntegrationEventContracts.EntitlementChanged,
            integrationEvent,
            occurredAtUtc,
            commandContext);
        var result = await repository.SaveEntitlementAsync(
            entitlement,
            storedRevision,
            commandIdentity,
            commandContext,
            outbox,
            cancellationToken);
        return new PromotionResponseResult<PromotionEntitlementResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    private async Task<PromotionEntitlement> RequireEntitlementAsync(
        Guid entitlementId,
        CancellationToken cancellationToken)
    {
        if (entitlementId == Guid.Empty)
        {
            throw new PromotionApplicationException(
                "Promotion.Entitlements",
                "PROMOTION_ENTITLEMENT_ID_INVALID",
                400,
                "Promotion entitlement ID is empty.",
                "Use the exact entitlement ID returned by the Promotion API.");
        }

        return await repository.GetEntitlementAsync(entitlementId, cancellationToken)
            ?? throw new PromotionApplicationException(
                "Promotion.Entitlements",
                "PROMOTION_ENTITLEMENT_NOT_FOUND",
                404,
                $"Promotion entitlement '{entitlementId}' was not found.",
                "Reload the listing entitlement inventory before submitting another command.");
    }
}
