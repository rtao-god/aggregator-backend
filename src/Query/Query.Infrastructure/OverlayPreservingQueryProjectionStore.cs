using System.Data;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

/// <summary>
/// Preserves the current promotion and visibility-safety components when Catalog activates a new
/// base projection. A durable block covers the base switch until the exact composite revision is
/// committed, and a catalog advisory lock serializes every Query projection writer.
/// </summary>
public sealed partial class OverlayPreservingQueryProjectionStore : IQueryProjectionStore
{
    private readonly NpgsqlQueryProjectionStore _inner;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IQueryIdFactory _idFactory;
    private readonly IQueryClock _clock;

    public OverlayPreservingQueryProjectionStore(
        NpgsqlQueryProjectionStore inner,
        NpgsqlDataSource dataSource,
        IQueryIdFactory idFactory,
        IQueryClock clock)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _idFactory = idFactory ?? throw new ArgumentNullException(nameof(idFactory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<QueryProjectionActivationResult> ActivateAsync(
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(inboxMessage);
        var catalogKey = activation.BaseProjection.CatalogKey;
        await using var lease = await QueryProjectionMutationLease.AcquireAsync(
            _dataSource,
            catalogKey,
            cancellationToken);
        var recomposition = await PrepareAsync(
            activation,
            inboxMessage,
            cancellationToken);
        var effectiveActivation = recomposition is null
            ? activation
            : await CreateOverlayPreservingActivationAsync(
                activation,
                recomposition,
                cancellationToken);
        var innerResult = await _inner.ActivateAsync(
            effectiveActivation,
            inboxMessage,
            cancellationToken);
        if (recomposition is null)
        {
            return innerResult;
        }

        if (innerResult.IgnoredStale)
        {
            await RemovePendingRecompositionAsync(
                recomposition,
                cancellationToken);
            return innerResult;
        }

        return await FinalizeAsync(
            effectiveActivation,
            inboxMessage,
            recomposition,
            innerResult,
            cancellationToken);
    }

    private async Task<QueryProjectionActivation> CreateOverlayPreservingActivationAsync(
        QueryProjectionActivation activation,
        PublicationRecompositionState state,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var promotionOverlay = await ReadOverlayAsync(
            connection,
            transaction,
            state.PromotionOverlayId,
            QueryOverlayKind.Promotion,
            activation.BaseProjection.CatalogKey,
            cancellationToken);
        var safetyOverlay = await ReadOverlayAsync(
            connection,
            transaction,
            state.SafetyOverlayId,
            QueryOverlayKind.VisibilitySafety,
            activation.BaseProjection.CatalogKey,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var finalRevision = CatalogPublicationOverlayRecomposer.Compose(
            activation.BaseProjection,
            promotionOverlay,
            safetyOverlay,
            _idFactory.Create(),
            RequireUtc(_clock.GetUtcNow()));
        return new QueryProjectionActivation(
            activation.BaseProjection,
            promotionOverlay,
            safetyOverlay,
            finalRevision);
    }

    private async Task<PublicationRecompositionState?> PrepareAsync(
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existingState = await ReadRecompositionStateAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            forUpdate: true,
            cancellationToken);
        if (existingState is not null)
        {
            ValidateStateIdentity(existingState, activation, inboxMessage);
            await transaction.CommitAsync(cancellationToken);
            return existingState;
        }

        var otherState = await ReadCatalogRecompositionEventAsync(
            connection,
            transaction,
            activation.BaseProjection.CatalogKey,
            cancellationToken);
        if (otherState is { } otherEventId && otherEventId != inboxMessage.EventId)
        {
            throw Failure(
                "QUERY_PUBLICATION_RECOMPOSITION_ALREADY_PENDING",
                503,
                $"Catalog '{activation.BaseProjection.CatalogKey}' already has pending publication recomposition '{otherEventId}'.",
                "Replay the pending Catalog publication event before processing another activation.");
        }

        var existingInboxDigest = await ReadPublicationInboxDigestAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            cancellationToken);
        if (existingInboxDigest is not null)
        {
            if (!string.Equals(
                    existingInboxDigest,
                    inboxMessage.PayloadDigest,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_PUBLICATION_EVENT_ID_REUSED",
                    409,
                    $"Catalog publication event '{inboxMessage.EventId}' already has another payload digest.",
                    "Reject the message and repair the Catalog publication outbox identity.");
            }

            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var current = await ReadCurrentComponentsAsync(
            connection,
            transaction,
            activation.BaseProjection.CatalogKey,
            cancellationToken);
        if (current is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await EnsurePromotionOverlayCompatibleWithBuildAsync(
            connection,
            transaction,
            current.PromotionOverlayId,
            activation.BaseProjection.Documents
                .Select(document => document.ListingId)
                .ToHashSet(),
            cancellationToken);

        var state = new PublicationRecompositionState(
            inboxMessage.EventId,
            activation.BaseProjection.CatalogKey,
            inboxMessage.PayloadDigest,
            current.PublicReadRevisionId,
            current.ActivationRevision,
            current.PromotionOverlayId,
            current.SafetyOverlayId,
            inboxMessage.ReceivedAtUtc);
        await InsertRecompositionStateAsync(
            connection,
            transaction,
            state,
            cancellationToken);
        await InsertPublicationBlockAsync(
            connection,
            transaction,
            state,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    private async Task<QueryProjectionActivationResult> FinalizeAsync(
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage,
        PublicationRecompositionState expectedState,
        QueryProjectionActivationResult innerResult,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var state = await ReadRecompositionStateAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            forUpdate: true,
            cancellationToken)
            ?? throw Failure(
                "QUERY_PUBLICATION_RECOMPOSITION_STATE_MISSING",
                500,
                $"Catalog publication event '{inboxMessage.EventId}' lost its durable recomposition state.",
                "Restore the Query database owner state before removing the public visibility block.");
        ValidateStateIdentity(state, activation, inboxMessage);
        if (state != expectedState)
        {
            throw Failure(
                "QUERY_PUBLICATION_RECOMPOSITION_STATE_CHANGED",
                409,
                $"Catalog publication event '{inboxMessage.EventId}' recomposition state changed during activation.",
                "Reload and replay the exact Catalog publication event.");
        }

        var current = await ReadCurrentActivationAsync(
            connection,
            transaction,
            activation.BaseProjection.CatalogKey,
            cancellationToken)
            ?? throw Failure(
                "QUERY_PUBLICATION_CURRENT_READ_MISSING",
                503,
                $"Catalog '{activation.BaseProjection.CatalogKey}' has no current public-read revision after base activation.",
                "Keep the catalog blocked and replay the Catalog publication projection.");
        if (current.PublicReadRevisionId != innerResult.PublicReadRevision.Id ||
            current.BaseProjectionId != innerResult.PublicReadRevision.BaseProjectionId ||
            current.SourcePublicationId != innerResult.PublicReadRevision.SourcePublicationId ||
            current.ActivationRevision != inboxMessage.ActivationRevision ||
            innerResult.PublicReadRevision.PromotionOverlayId != state.PromotionOverlayId ||
            innerResult.PublicReadRevision.SafetyOverlayId != state.SafetyOverlayId)
        {
            throw Failure(
                "QUERY_PUBLICATION_CURRENT_COMPOSITE_MISMATCH",
                409,
                $"Catalog '{activation.BaseProjection.CatalogKey}' current composite does not match publication event '{inboxMessage.EventId}'.",
                "Keep the catalog blocked and inspect Query publication ordering and overlay preservation.");
        }

        await DeletePublicationBlockAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            cancellationToken);
        await DeleteRecompositionStateAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return innerResult;
    }

    private async Task RemovePendingRecompositionAsync(
        PublicationRecompositionState state,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await DeletePublicationBlockAsync(
            connection,
            transaction,
            state.SourceEventId,
            cancellationToken);
        await DeleteRecompositionStateAsync(
            connection,
            transaction,
            state.SourceEventId,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_PUBLICATION_RECOMPOSITION_TIME_NOT_UTC",
                500,
                "Query publication recomposition clock returned a non-UTC timestamp.",
                "Configure the Query worker clock to return UTC timestamps.");
        }

        return value;
    }

    private static void ValidateStateIdentity(
        PublicationRecompositionState state,
        QueryProjectionActivation activation,
        QueryInboxMessage inboxMessage)
    {
        if (!string.Equals(
                state.CatalogKey,
                activation.BaseProjection.CatalogKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                state.PayloadDigest,
                inboxMessage.PayloadDigest,
                StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_PUBLICATION_RECOMPOSITION_IDENTITY_CONFLICT",
                409,
                $"Catalog publication event '{inboxMessage.EventId}' conflicts with its durable recomposition state.",
                "Keep the catalog blocked and inspect the exact Catalog event payload.");
        }
    }

    private static QueryProjectionException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction) =>
        new(
            "Query.PublicationRecomposition",
            code,
            statusCode,
            message,
            requiredAction);

    private sealed record PublicationRecompositionState(
        Guid SourceEventId,
        string CatalogKey,
        string PayloadDigest,
        Guid PreviousPublicReadRevisionId,
        long PreviousPointerActivationRevision,
        Guid PromotionOverlayId,
        Guid SafetyOverlayId,
        DateTimeOffset CreatedAtUtc);

    private sealed record CurrentComponents(
        Guid PublicReadRevisionId,
        long ActivationRevision,
        Guid PromotionOverlayId,
        Guid SafetyOverlayId);

    private sealed record CurrentActivation(
        Guid PublicReadRevisionId,
        Guid BaseProjectionId,
        Guid SourcePublicationId,
        long ActivationRevision);
}
