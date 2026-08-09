using System.Data;
using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public sealed partial class PostgresVisibilitySafetyProjectionStore
{
    private async Task<BeginResult> BeginAsync(
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var existingInbox = await ReadInboxAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            forUpdate: true,
            cancellationToken);
        if (existingInbox is not null)
        {
            if (!string.Equals(
                    existingInbox.PayloadDigest,
                    inboxMessage.PayloadDigest,
                    StringComparison.Ordinal))
            {
                await EnsureBlockAsync(
                    connection,
                    transaction,
                    suppression,
                    inboxMessage,
                    "event_payload_digest_conflict",
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BeginResult(
                    null,
                    $"Visibility event '{inboxMessage.EventId}' was already received with another payload digest.");
            }

            if (string.Equals(existingInbox.ProcessingState, "pending", StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken);
                return BeginResult.Pending;
            }

            if (existingInbox.ResultPublicReadRevisionId is not { } existingRevisionId)
            {
                throw Failure(
                    "QUERY_VISIBILITY_INBOX_RESULT_MISSING",
                    500,
                    $"Completed visibility inbox event '{inboxMessage.EventId}' has no result revision.",
                    "Restore the Query database from owner backup or rebuild the visibility projection.");
            }

            var existingRevision = await LoadPublicReadRevisionAsync(
                connection,
                transaction,
                existingRevisionId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BeginResult(
                new VisibilitySafetyProjectionResult(
                    existingRevision,
                    string.Equals(existingInbox.ProcessingState, "completed", StringComparison.Ordinal)
                        ? VisibilitySafetyProjectionDisposition.Replayed
                        : VisibilitySafetyProjectionDisposition.IgnoredStale),
                null);
        }

        var currentState = await ReadSuppressionStateAsync(
            connection,
            transaction,
            suppression.SuppressionId,
            forUpdate: true,
            cancellationToken);
        if (currentState is not null &&
            suppression.AggregateRevision <= currentState.Value.AggregateRevision)
        {
            if (suppression.AggregateRevision == currentState.Value.AggregateRevision &&
                !string.Equals(
                    inboxMessage.PayloadDigest,
                    currentState.PayloadDigest,
                    StringComparison.Ordinal))
            {
                await InsertPendingInboxAsync(
                    connection,
                    transaction,
                    suppression,
                    inboxMessage,
                    cancellationToken);
                await EnsureBlockAsync(
                    connection,
                    transaction,
                    suppression,
                    inboxMessage,
                    "suppression_revision_digest_conflict",
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new BeginResult(
                    null,
                    $"Suppression '{suppression.SuppressionId}' revision '{suppression.AggregateRevision}' already has another payload digest.");
            }

            var currentRevision = await LoadCurrentPublicReadRevisionAsync(
                connection,
                transaction,
                suppression.CatalogKey,
                lockPointer: false,
                cancellationToken)
                ?? throw Failure(
                    "QUERY_VISIBILITY_PUBLIC_READ_MISSING",
                    503,
                    $"Catalog '{suppression.CatalogKey}' has no current public-read revision.",
                    "Activate a valid Catalog publication before consuming visibility events.");
            await InsertFinalInboxAsync(
                connection,
                transaction,
                suppression,
                inboxMessage,
                "ignored_stale",
                currentRevision.Id,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new BeginResult(
                new VisibilitySafetyProjectionResult(
                    currentRevision,
                    VisibilitySafetyProjectionDisposition.IgnoredStale),
                null);
        }

        await InsertPendingInboxAsync(
            connection,
            transaction,
            suppression,
            inboxMessage,
            cancellationToken);
        await EnsureBlockAsync(
            connection,
            transaction,
            suppression,
            inboxMessage,
            "visibility_event_pending_materialization",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return BeginResult.Pending;
    }

    private async Task<VisibilitySafetyProjectionResult> CompleteAsync(
        QueryVisibilitySuppression suppression,
        VisibilitySuppressionInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var inbox = await ReadInboxAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            forUpdate: true,
            cancellationToken)
            ?? throw Failure(
                "QUERY_VISIBILITY_INBOX_MISSING",
                500,
                $"Visibility event '{inboxMessage.EventId}' has no durable inbox record.",
                "Re-deliver the exact Catalog event through the Query visibility worker.");
        if (!string.Equals(inbox.PayloadDigest, inboxMessage.PayloadDigest, StringComparison.Ordinal))
        {
            throw Failure(
                "QUERY_VISIBILITY_INBOX_DIGEST_CONFLICT",
                409,
                $"Visibility event '{inboxMessage.EventId}' inbox digest changed between processing phases.",
                "Keep the catalog blocked and inspect Query persistence for corruption.");
        }

        if (!string.Equals(inbox.ProcessingState, "pending", StringComparison.Ordinal))
        {
            if (inbox.ResultPublicReadRevisionId is not { } completedRevisionId)
            {
                throw Failure(
                    "QUERY_VISIBILITY_INBOX_RESULT_MISSING",
                    500,
                    $"Visibility event '{inboxMessage.EventId}' has terminal state without a result revision.",
                    "Restore the Query database from owner backup or rebuild the visibility projection.");
            }

            var completedRevision = await LoadPublicReadRevisionAsync(
                connection,
                transaction,
                completedRevisionId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new VisibilitySafetyProjectionResult(
                completedRevision,
                string.Equals(inbox.ProcessingState, "completed", StringComparison.Ordinal)
                    ? VisibilitySafetyProjectionDisposition.Replayed
                    : VisibilitySafetyProjectionDisposition.IgnoredStale);
        }

        var currentContext = await ReadCurrentContextAsync(
            connection,
            transaction,
            suppression.CatalogKey,
            cancellationToken)
            ?? throw Failure(
                "QUERY_VISIBILITY_PUBLIC_READ_MISSING",
                503,
                $"Catalog '{suppression.CatalogKey}' has no current public-read revision.",
                "Activate a valid Catalog publication; the visibility block must remain until recovery.");
        var currentState = await ReadSuppressionStateAsync(
            connection,
            transaction,
            suppression.SuppressionId,
            forUpdate: true,
            cancellationToken);
        if (currentState is not null &&
            suppression.AggregateRevision <= currentState.Value.AggregateRevision)
        {
            if (suppression.AggregateRevision == currentState.Value.AggregateRevision &&
                !string.Equals(
                    inboxMessage.PayloadDigest,
                    currentState.PayloadDigest,
                    StringComparison.Ordinal))
            {
                throw Failure(
                    "QUERY_VISIBILITY_REVISION_DIGEST_CONFLICT",
                    409,
                    $"Suppression '{suppression.SuppressionId}' revision '{suppression.AggregateRevision}' conflicts with persisted Query state.",
                    "Keep the catalog blocked and inspect the Catalog producer event and Query state.");
            }

            await CompleteInboxAsync(
                connection,
                transaction,
                inboxMessage.EventId,
                "ignored_stale",
                currentContext.Revision.Id,
                RequireUtc(_clock.GetUtcNow()),
                cancellationToken);
            await DeleteBlockAsync(
                connection,
                transaction,
                inboxMessage.EventId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new VisibilitySafetyProjectionResult(
                currentContext.Revision,
                VisibilitySafetyProjectionDisposition.IgnoredStale);
        }

        if (currentState is null)
        {
            suppression.EnsureValidInitialProjection();
        }
        else
        {
            currentState.Value.EnsureCanAdvanceTo(suppression);
        }

        if (suppression.State == QueryVisibilitySuppressionState.Active)
        {
            await EnsureActiveTargetExistsAsync(
                connection,
                transaction,
                currentContext.Revision.BaseProjectionId,
                suppression,
                cancellationToken);
        }

        await UpsertSuppressionStateAsync(
            connection,
            transaction,
            suppression,
            inboxMessage,
            cancellationToken);
        var activeSuppressions = await ReadActiveSuppressionsAsync(
            connection,
            transaction,
            suppression.CatalogKey,
            cancellationToken);
        var builtAtUtc = RequireUtc(_clock.GetUtcNow());
        var materialization = VisibilitySafetyProjectionBuilder.Build(
            currentContext.Revision,
            currentContext.BaseProjectionDigest,
            currentContext.PromotionOverlayDigest,
            checked(currentContext.SafetySourceRevision + 1),
            activeSuppressions,
            _idFactory.Create(),
            _idFactory.Create(),
            builtAtUtc);

        await InsertOverlayAsync(
            connection,
            transaction,
            materialization,
            cancellationToken);
        await InsertPublicReadRevisionAsync(
            connection,
            transaction,
            materialization.PublicReadRevision,
            cancellationToken);
        var nextActivationRevision = checked(currentContext.PointerActivationRevision + 1);
        await UpdateCurrentPointerAsync(
            connection,
            transaction,
            materialization.PublicReadRevision,
            nextActivationRevision,
            builtAtUtc,
            cancellationToken);
        await CompleteInboxAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            "completed",
            materialization.PublicReadRevision.Id,
            builtAtUtc,
            cancellationToken);
        await DeleteBlockAsync(
            connection,
            transaction,
            inboxMessage.EventId,
            cancellationToken);
        await QueryPublicReadActivationOutboxWriter.InsertAsync(
            connection,
            transaction,
            materialization.PublicReadRevision,
            nextActivationRevision,
            builtAtUtc,
            inboxMessage.CorrelationId,
            inboxMessage.EventId,
            _idFactory,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VisibilitySafetyProjectionResult(
            materialization.PublicReadRevision,
            VisibilitySafetyProjectionDisposition.Activated);
    }

    private static DateTimeOffset RequireUtc(DateTimeOffset value)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw Failure(
                "QUERY_VISIBILITY_CLOCK_NOT_UTC",
                500,
                "Query visibility store clock returned a non-UTC timestamp.",
                "Configure the Query worker clock to return UTC timestamps.");
        }

        return value;
    }
}
