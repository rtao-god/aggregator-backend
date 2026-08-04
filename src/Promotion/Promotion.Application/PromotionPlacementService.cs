using Aggregator.Promotion.Contracts;
using Aggregator.Promotion.Domain;

namespace Aggregator.Promotion.Application;

public sealed class PromotionPlacementService(
    IPromotionRepository repository,
    IPromotionIdSource idSource,
    IPromotionClock clock)
{
    public async Task<PromotionResponseResult<SponsoredPlacementResponse>> CreateAsync(
        CreateSponsoredPlacementRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        var entitlement = await RequireEntitlementAsync(request.EntitlementId, cancellationToken);
        var product = await RequireProductAsync(entitlement.ProductKey, cancellationToken);
        var eligibility = await RequireEligibilityAsync(
            request.CatalogKey,
            entitlement.ListingId,
            cancellationToken);
        var scopeType = PromotionContractMapper.ToDomain(request.ScopeType);
        var window = PromotionWindow.Create(request.StartsAtUtc, request.EndsAtUtc);
        var contentDigest = BuildRevisionContentDigest(
            request.CatalogKey,
            scopeType,
            request.ScopeKey,
            request.LocaleScope,
            window,
            request.PriorityBand,
            request.CapacitySlot,
            request.PresentationLabelKey);
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = "sponsored-placement-create",
            request.EntitlementId,
            request.CatalogKey,
            scopeType,
            request.ScopeKey,
            localeScope = RequireLocales(request.LocaleScope),
            request.StartsAtUtc,
            request.EndsAtUtc,
            request.PriorityBand,
            request.CapacitySlot,
            request.PresentationLabelKey,
            request.AuditReason,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.entitlement.{request.EntitlementId:N}.placement.create",
            idempotencyKey,
            requestDigest);
        var occurredAtUtc = clock.GetUtcNow();
        var placement = SponsoredPlacement.Create(
            idSource.CreateId(),
            idSource.CreateId(),
            entitlement,
            product,
            eligibility,
            request.CatalogKey,
            scopeType,
            request.ScopeKey,
            request.LocaleScope,
            window,
            request.PriorityBand,
            request.CapacitySlot,
            request.PresentationLabelKey,
            commandContext.Actor.Id,
            request.AuditReason,
            occurredAtUtc,
            contentDigest);
        await EnsureCapacityAsync(placement, excludedPlacementId: null, cancellationToken);
        var outbox = CreateOutbox(placement, occurredAtUtc, commandContext);
        var result = await repository.AddPlacementAsync(
            placement,
            commandIdentity,
            commandContext,
            outbox,
            cancellationToken);
        return new PromotionResponseResult<SponsoredPlacementResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    public async Task<PromotionResponseResult<SponsoredPlacementResponse>> ReviseAsync(
        Guid placementId,
        CreateSponsoredPlacementRevisionRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        var placement = await RequirePlacementAsync(placementId, cancellationToken);
        var entitlement = await RequireEntitlementAsync(placement.EntitlementId, cancellationToken);
        var product = await RequireProductAsync(placement.ProductKey, cancellationToken);
        var eligibility = await RequireEligibilityAsync(
            placement.CurrentRevision.CatalogKey,
            placement.ListingId,
            cancellationToken);
        var storedRevision = placement.AggregateRevision;
        var scopeType = PromotionContractMapper.ToDomain(request.ScopeType);
        var window = PromotionWindow.Create(request.StartsAtUtc, request.EndsAtUtc);
        var contentDigest = BuildRevisionContentDigest(
            placement.CurrentRevision.CatalogKey,
            scopeType,
            request.ScopeKey,
            request.LocaleScope,
            window,
            request.PriorityBand,
            request.CapacitySlot,
            request.PresentationLabelKey);
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = "sponsored-placement-revision-create",
            placementId,
            request.ExpectedAggregateRevision,
            scopeType,
            request.ScopeKey,
            localeScope = RequireLocales(request.LocaleScope),
            request.StartsAtUtc,
            request.EndsAtUtc,
            request.PriorityBand,
            request.CapacitySlot,
            request.PresentationLabelKey,
            request.AuditReason,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.placement.{placementId:N}.revision.create",
            idempotencyKey,
            requestDigest);
        var occurredAtUtc = clock.GetUtcNow();
        placement.Revise(
            request.ExpectedAggregateRevision,
            idSource.CreateId(),
            entitlement,
            product,
            eligibility,
            scopeType,
            request.ScopeKey,
            request.LocaleScope,
            window,
            request.PriorityBand,
            request.CapacitySlot,
            request.PresentationLabelKey,
            commandContext.Actor.Id,
            request.AuditReason,
            occurredAtUtc,
            contentDigest);
        await EnsureCapacityAsync(placement, placement.Id, cancellationToken);
        var outbox = CreateOutbox(placement, occurredAtUtc, commandContext);
        var result = await repository.SavePlacementAsync(
            placement,
            storedRevision,
            commandIdentity,
            commandContext,
            outbox,
            cancellationToken);
        return new PromotionResponseResult<SponsoredPlacementResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    public Task<PromotionResponseResult<SponsoredPlacementResponse>> PauseAsync(
        Guid placementId,
        ChangeSponsoredPlacementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ChangeStateAsync(
            placementId,
            request,
            commandContext,
            idempotencyKey,
            "pause",
            static (placement, expectedRevision, context, changedAtUtc) =>
                placement.Pause(
                    expectedRevision,
                    context.Actor.Id,
                    context.AuditReason,
                    changedAtUtc),
            requiresCapacityCheck: false,
            cancellationToken);

    public async Task<PromotionResponseResult<SponsoredPlacementResponse>> ResumeAsync(
        Guid placementId,
        ChangeSponsoredPlacementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        var placement = await RequirePlacementAsync(placementId, cancellationToken);
        var entitlement = await RequireEntitlementAsync(placement.EntitlementId, cancellationToken);
        var product = await RequireProductAsync(placement.ProductKey, cancellationToken);
        var eligibility = await RequireEligibilityAsync(
            placement.CurrentRevision.CatalogKey,
            placement.ListingId,
            cancellationToken);
        var storedRevision = placement.AggregateRevision;
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = "sponsored-placement-resume",
            placementId,
            request.ExpectedAggregateRevision,
            request.AuditReason,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.placement.{placementId:N}.resume",
            idempotencyKey,
            requestDigest);
        var occurredAtUtc = clock.GetUtcNow();
        placement.Resume(
            request.ExpectedAggregateRevision,
            entitlement,
            eligibility,
            product,
            commandContext.Actor.Id,
            request.AuditReason,
            occurredAtUtc);
        await EnsureCapacityAsync(placement, placement.Id, cancellationToken);
        var outbox = CreateOutbox(placement, occurredAtUtc, commandContext);
        var result = await repository.SavePlacementAsync(
            placement,
            storedRevision,
            commandIdentity,
            commandContext,
            outbox,
            cancellationToken);
        return new PromotionResponseResult<SponsoredPlacementResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    public Task<PromotionResponseResult<SponsoredPlacementResponse>> RevokeAsync(
        Guid placementId,
        ChangeSponsoredPlacementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        CancellationToken cancellationToken) =>
        ChangeStateAsync(
            placementId,
            request,
            commandContext,
            idempotencyKey,
            "revoke",
            static (placement, expectedRevision, context, changedAtUtc) =>
                placement.Revoke(
                    expectedRevision,
                    context.Actor.Id,
                    context.AuditReason,
                    changedAtUtc),
            requiresCapacityCheck: false,
            cancellationToken);

    public async Task<SponsoredPlacementResponse> GetAsync(
        Guid placementId,
        CancellationToken cancellationToken) =>
        PromotionContractMapper.ToResponse(await RequirePlacementAsync(placementId, cancellationToken));

    public async Task<PromotionPlacementCalendarResponse> ReadCalendarAsync(
        string catalogKey,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        var window = PromotionWindow.Create(fromUtc, toUtc);
        if (string.IsNullOrWhiteSpace(catalogKey))
        {
            throw new PromotionApplicationException(
                "Promotion.Placements",
                "PROMOTION_CATALOG_KEY_REQUIRED",
                400,
                "Catalog key is required.",
                "Use the exact Catalog key from product configuration.");
        }

        var placements = await repository.ListPlacementsAsync(
            catalogKey,
            window.StartsAtUtc,
            window.EndsAtUtc,
            cancellationToken);
        return new PromotionPlacementCalendarResponse(
            catalogKey.Trim().ToLowerInvariant(),
            window.StartsAtUtc,
            window.EndsAtUtc,
            placements
                .OrderBy(item => item.CurrentRevision.EffectiveWindow.StartsAtUtc)
                .ThenBy(item => item.CurrentRevision.CapacitySlot)
                .ThenBy(item => item.Id)
                .Select(PromotionContractMapper.ToResponse)
                .ToArray());
    }

    private async Task<PromotionResponseResult<SponsoredPlacementResponse>> ChangeStateAsync(
        Guid placementId,
        ChangeSponsoredPlacementStateRequest request,
        PromotionCommandContext commandContext,
        string idempotencyKey,
        string operation,
        Action<SponsoredPlacement, long, PlacementTransitionContext, DateTimeOffset> transition,
        bool requiresCapacityCheck,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(commandContext);
        ArgumentNullException.ThrowIfNull(transition);
        var placement = await RequirePlacementAsync(placementId, cancellationToken);
        var storedRevision = placement.AggregateRevision;
        var requestDigest = PromotionCanonicalJson.ComputeDigest(new
        {
            operation = $"sponsored-placement-{operation}",
            placementId,
            request.ExpectedAggregateRevision,
            request.AuditReason,
        });
        var commandIdentity = PromotionCommandIdentity.Create(
            $"promotion.placement.{placementId:N}.{operation}",
            idempotencyKey,
            requestDigest);
        var occurredAtUtc = clock.GetUtcNow();
        transition(
            placement,
            request.ExpectedAggregateRevision,
            new PlacementTransitionContext(commandContext.Actor, request.AuditReason),
            occurredAtUtc);
        if (requiresCapacityCheck)
        {
            await EnsureCapacityAsync(placement, placement.Id, cancellationToken);
        }

        var outbox = CreateOutbox(placement, occurredAtUtc, commandContext);
        var result = await repository.SavePlacementAsync(
            placement,
            storedRevision,
            commandIdentity,
            commandContext,
            outbox,
            cancellationToken);
        return new PromotionResponseResult<SponsoredPlacementResponse>(
            PromotionContractMapper.ToResponse(result.Aggregate),
            result.Replayed);
    }

    private async Task EnsureCapacityAsync(
        SponsoredPlacement placement,
        Guid? excludedPlacementId,
        CancellationToken cancellationToken)
    {
        if (await repository.HasPlacementConflictAsync(
            placement,
            excludedPlacementId,
            cancellationToken))
        {
            throw new PromotionApplicationException(
                "Promotion.Placements",
                "PROMOTION_CAPACITY_CONFLICT",
                409,
                "Sponsored placement overlaps another active or scheduled placement in the same capacity slot.",
                "Choose another slot or a non-overlapping effective interval.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["catalogKey"] = placement.CurrentRevision.CatalogKey,
                    ["scopeType"] = placement.CurrentRevision.ScopeType,
                    ["scopeKey"] = placement.CurrentRevision.ScopeKey,
                    ["capacitySlot"] = placement.CurrentRevision.CapacitySlot,
                    ["startsAtUtc"] = placement.CurrentRevision.EffectiveWindow.StartsAtUtc,
                    ["endsAtUtc"] = placement.CurrentRevision.EffectiveWindow.EndsAtUtc,
                });
        }
    }

    private PromotionOutboxMessage CreateOutbox(
        SponsoredPlacement placement,
        DateTimeOffset occurredAtUtc,
        PromotionCommandContext commandContext)
    {
        var eventId = idSource.CreateId();
        var integrationEvent = PromotionContractMapper.ToEvent(placement, eventId, occurredAtUtc);
        return PromotionOutboxMessageFactory.Create(
            eventId,
            PromotionIntegrationEventTypes.PlacementChanged,
            PromotionIntegrationEventContracts.PlacementChanged,
            integrationEvent,
            occurredAtUtc,
            commandContext);
    }

    private async Task<PromotionEntitlement> RequireEntitlementAsync(
        Guid entitlementId,
        CancellationToken cancellationToken) =>
        await repository.GetEntitlementAsync(entitlementId, cancellationToken)
        ?? throw new PromotionApplicationException(
            "Promotion.Entitlements",
            "PROMOTION_ENTITLEMENT_NOT_FOUND",
            404,
            $"Promotion entitlement '{entitlementId}' was not found.",
            "Reload the exact listing entitlement before creating or changing a placement.");

    private async Task<PromotionProduct> RequireProductAsync(
        string productKey,
        CancellationToken cancellationToken) =>
        await repository.GetProductByKeyAsync(productKey, cancellationToken)
        ?? throw new PromotionApplicationException(
            "Promotion.Products",
            "PROMOTION_PRODUCT_NOT_FOUND",
            404,
            $"Promotion product '{productKey}' was not found.",
            "Restore or create the exact Promotion product before changing a placement.");

    private async Task<ListingPromotionEligibility> RequireEligibilityAsync(
        string catalogKey,
        Guid listingId,
        CancellationToken cancellationToken) =>
        await repository.GetEligibilityAsync(catalogKey, listingId, cancellationToken)
        ?? throw new PromotionApplicationException(
            "Promotion.EligibilityProjection",
            "PROMOTION_ELIGIBILITY_PROJECTION_UNAVAILABLE",
            503,
            $"Promotion eligibility projection for listing '{listingId}' is unavailable.",
            "Replay the exact Catalog eligibility event before changing Promotion state.");

    private async Task<SponsoredPlacement> RequirePlacementAsync(
        Guid placementId,
        CancellationToken cancellationToken)
    {
        if (placementId == Guid.Empty)
        {
            throw new PromotionApplicationException(
                "Promotion.Placements",
                "PROMOTION_PLACEMENT_ID_INVALID",
                400,
                "Sponsored placement ID is empty.",
                "Use the exact placement ID returned by the Promotion API.");
        }

        return await repository.GetPlacementAsync(placementId, cancellationToken)
            ?? throw new PromotionApplicationException(
                "Promotion.Placements",
                "PROMOTION_PLACEMENT_NOT_FOUND",
                404,
                $"Sponsored placement '{placementId}' was not found.",
                "Reload the Promotion placement inventory before submitting another command.");
    }

    private static string BuildRevisionContentDigest(
        string catalogKey,
        PlacementScopeType scopeType,
        string scopeKey,
        IReadOnlyList<string>? localeScope,
        PromotionWindow window,
        int priorityBand,
        int capacitySlot,
        string presentationLabelKey) =>
        PromotionCanonicalJson.ComputeDigest(new
        {
            catalogKey,
            scopeType,
            scopeKey,
            localeScope = RequireLocales(localeScope),
            window.StartsAtUtc,
            window.EndsAtUtc,
            priorityBand,
            capacitySlot,
            presentationLabelKey,
        });

    private static IReadOnlyList<string> RequireLocales(IReadOnlyList<string>? localeScope)
    {
        if (localeScope is null)
        {
            throw new PromotionApplicationException(
                "Promotion.Placements",
                "PROMOTION_PLACEMENT_LOCALE_REQUIRED",
                400,
                "Sponsored placement locale scope is required.",
                "Submit one or more exact locale identifiers.");
        }

        return localeScope.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed record PlacementTransitionContext(PromotionActor Actor, string AuditReason);
}
