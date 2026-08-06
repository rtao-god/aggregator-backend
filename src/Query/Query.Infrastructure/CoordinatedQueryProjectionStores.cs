using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

/// <summary>
/// Serializes Promotion mutations with Catalog publication recomposition so an overlay event
/// cannot be lost between the new base activation and final composite pointer switch.
/// </summary>
public sealed class CoordinatedPromotionPlacementProjectionStore :
    IPromotionPlacementProjectionStore
{
    private readonly PostgresPromotionOverlayProjectionStore _inner;
    private readonly NpgsqlDataSource _dataSource;

    public CoordinatedPromotionPlacementProjectionStore(
        PostgresPromotionOverlayProjectionStore inner,
        NpgsqlDataSource dataSource)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<PromotionPlacementProjectionResult> ApplyAsync(
        QueryPromotionPlacement change,
        PromotionPlacementInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        await using var lease = await QueryProjectionMutationLease.AcquireAsync(
            _dataSource,
            change.CatalogKey,
            cancellationToken);
        await lease.EnsureNoPublicationRecompositionAsync(cancellationToken);
        return await _inner.ApplyAsync(change, inboxMessage, cancellationToken);
    }
}

/// <summary>
/// Serializes Catalog safety mutations with publication recomposition while preserving the
/// block-first visibility protocol owned by the inner safety store.
/// </summary>
public sealed class CoordinatedVisibilitySafetyProjectionStore :
    IVisibilitySafetyProjectionStore
{
    private readonly PostgresVisibilitySafetyProjectionStore _inner;
    private readonly NpgsqlDataSource _dataSource;

    public CoordinatedVisibilitySafetyProjectionStore(
        PostgresVisibilitySafetyProjectionStore inner,
        NpgsqlDataSource dataSource)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<VisibilitySafetyProjectionResult> ApplyAsync(
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(suppression);
        await using var lease = await QueryProjectionMutationLease.AcquireAsync(
            _dataSource,
            suppression.CatalogKey,
            cancellationToken);
        await lease.EnsureNoPublicationRecompositionAsync(cancellationToken);
        return await _inner.ApplyAsync(suppression, inboxMessage, cancellationToken);
    }
}
