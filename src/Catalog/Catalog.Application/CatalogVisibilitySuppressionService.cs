using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

/// <summary>
/// Owns emergency public-visibility suppression commands and publishes only their minimal public state.
/// </summary>
public sealed class CatalogVisibilitySuppressionService(
    ICatalogVisibilitySuppressionRepository repository,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    public Task<PublicVisibilitySuppressionResponse> CreateActiveAsync(
        string catalogKey,
        CreatePublicVisibilitySuppressionRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken) =>
        CreateActiveAsync(
            catalogKey,
            request,
            actor,
            CatalogEventContext.StartRoot(),
            cancellationToken);

    public async Task<PublicVisibilitySuppressionResponse> CreateActiveAsync(
        string catalogKey,
        CreatePublicVisibilitySuppressionRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Target);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);

        var ownerCatalogKey = CatalogKey.Create(catalogKey);
        var target = CatalogVisibilitySuppressionContractMapper.ToDomain(request.Target);
        await repository.EnsureTargetExistsAsync(ownerCatalogKey, target, cancellationToken);

        var changedAtUtc = timeProvider.GetUtcNow();
        var requested = PublicVisibilitySuppression.Request(
            idSource.CreateId(),
            ownerCatalogKey,
            target,
            request.PublicReasonClass,
            request.PrivateEvidenceReference,
            CatalogVisibilitySuppressionContractMapper.ToDomain(request.ResponseMode),
            changedAtUtc,
            request.ExpiresAtUtc,
            actor.Id,
            request.Reason,
            changedAtUtc);
        var active = requested.Activate(
            requested.Revision,
            actor.Id,
            request.Reason,
            changedAtUtc);
        var integrationEvent = CatalogVisibilitySuppressionContractMapper.ToIntegrationEvent(
            idSource.CreateId(),
            active);
        var outboxMessage = CatalogOutboxMessageFactory.Create(
            integrationEvent.EventId,
            CatalogIntegrationEventTypes.PublicVisibilitySuppressionChanged,
            CatalogIntegrationEventContracts.PublicVisibilitySuppressionChanged,
            integrationEvent,
            active.ChangedAtUtc,
            eventContext);

        await repository.CreateActiveAsync(
            requested,
            active,
            outboxMessage,
            cancellationToken);
        return CatalogVisibilitySuppressionContractMapper.ToResponse(active);
    }

    public Task<PublicVisibilitySuppressionResponse> ResolveAsync(
        string catalogKey,
        Guid suppressionId,
        ResolvePublicVisibilitySuppressionRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken) =>
        ResolveAsync(
            catalogKey,
            suppressionId,
            request,
            actor,
            CatalogEventContext.StartRoot(),
            cancellationToken);

    public async Task<PublicVisibilitySuppressionResponse> ResolveAsync(
        string catalogKey,
        Guid suppressionId,
        ResolvePublicVisibilitySuppressionRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        if (suppressionId == Guid.Empty)
        {
            throw new ArgumentException("Suppression ID is required.", nameof(suppressionId));
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);
        var ownerCatalogKey = CatalogKey.Create(catalogKey);
        var current = await repository.GetAsync(suppressionId, cancellationToken)
            ?? throw new CatalogNotFoundException("public-visibility-suppression", suppressionId);
        if (current.CatalogKey != ownerCatalogKey)
        {
            throw new CatalogConflictException(
                $"Suppression '{suppressionId}' belongs to catalog '{current.CatalogKey}', not '{ownerCatalogKey}'.");
        }

        var resolved = current.Resolve(
            request.ExpectedRevision,
            actor.Id,
            request.Reason,
            timeProvider.GetUtcNow());
        var integrationEvent = CatalogVisibilitySuppressionContractMapper.ToIntegrationEvent(
            idSource.CreateId(),
            resolved);
        var outboxMessage = CatalogOutboxMessageFactory.Create(
            integrationEvent.EventId,
            CatalogIntegrationEventTypes.PublicVisibilitySuppressionChanged,
            CatalogIntegrationEventContracts.PublicVisibilitySuppressionChanged,
            integrationEvent,
            resolved.ChangedAtUtc,
            eventContext);
        await repository.ResolveAsync(resolved, outboxMessage, cancellationToken);
        return CatalogVisibilitySuppressionContractMapper.ToResponse(resolved);
    }
}
