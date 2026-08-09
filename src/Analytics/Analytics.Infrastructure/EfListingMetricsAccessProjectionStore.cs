using System.Data;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Analytics.Infrastructure;

/// <summary>Atomically persists Catalog grant inbox state and the Analytics-local grant projection.</summary>
public sealed class EfListingMetricsAccessProjectionStore(AnalyticsAccessProjectionDbContext dbContext)
    : IListingMetricsAccessProjectionStore
{
    public async Task<ListingMetricsAccessProjectionResult> ApplyAsync(
        ListingMetricsAccessProjectionChange change,
        DateTimeOffset receivedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(change.Projection);
        var projection = change.Projection;
        var normalizedProjectionDigest = AnalyticsDomainRules.RequireDigest(
            change.ProjectionDigest,
            nameof(change.ProjectionDigest));
        var normalizedPayloadDigest = AnalyticsDomainRules.RequireDigest(
            change.PayloadDigest,
            nameof(change.PayloadDigest));
        if (!string.Equals(
                normalizedProjectionDigest,
                projection.ProjectionDigest,
                StringComparison.Ordinal))
        {
            throw Failure(
                "ANALYTICS_ACCESS_PROJECTION_IDENTITY_MISMATCH",
                409,
                "The access projection does not match its application-owned projection digest.",
                "Stop Catalog access event consumption and inspect the Analytics projection mapper.",
                change);
        }

        if (!string.Equals(
                normalizedPayloadDigest,
                projection.SourcePayloadDigest,
                StringComparison.Ordinal))
        {
            throw Failure(
                "ANALYTICS_ACCESS_PAYLOAD_IDENTITY_MISMATCH",
                409,
                "The access projection and inbox message do not carry the same producer payload digest.",
                "Stop Catalog access event consumption and inspect the producer message envelope.",
                change);
        }

        AnalyticsDomainRules.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        await AcquireOwnerLocksAsync(change, cancellationToken);

        var existingInbox = await dbContext.ListingAccessInboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MessageId == change.MessageId,
                cancellationToken);
        if (existingInbox is not null)
        {
            EnsureSameInbox(existingInbox, change);
            await transaction.CommitAsync(cancellationToken);
            return new ListingMetricsAccessProjectionResult(
                change.Projection,
                ListingMetricsAccessProjectionDisposition.Replayed);
        }

        var row = await dbContext.ListingAccessProjections
            .SingleOrDefaultAsync(
                candidate => candidate.GrantId == projection.GrantId,
                cancellationToken);
        ListingMetricsAccessProjection resultProjection;
        ListingMetricsAccessProjectionDisposition disposition;
        if (row is null)
        {
            if (projection.SourceAggregateRevision != 1)
            {
                throw RevisionGap(change, currentRevision: 0);
            }

            row = ToRow(projection, normalizedProjectionDigest);
            dbContext.ListingAccessProjections.Add(row);
            resultProjection = projection;
            disposition = ListingMetricsAccessProjectionDisposition.Applied;
        }
        else
        {
            var currentProjection = RestoreProjection(row);
            EnsureImmutableIdentity(currentProjection, projection, change);
            if (projection.SourceAggregateRevision == currentProjection.SourceAggregateRevision)
            {
                EnsureSameProjection(
                    currentProjection,
                    row.ProjectionDigest,
                    projection,
                    normalizedProjectionDigest,
                    change);
                resultProjection = currentProjection;
                disposition = ListingMetricsAccessProjectionDisposition.Replayed;
            }
            else if (projection.SourceAggregateRevision < currentProjection.SourceAggregateRevision)
            {
                resultProjection = currentProjection;
                disposition = ListingMetricsAccessProjectionDisposition.IgnoredStale;
            }
            else
            {
                var nextRevision = checked(currentProjection.SourceAggregateRevision + 1);
                if (projection.SourceAggregateRevision != nextRevision)
                {
                    throw RevisionGap(change, currentProjection.SourceAggregateRevision);
                }

                Apply(row, projection, normalizedProjectionDigest);
                resultProjection = projection;
                disposition = ListingMetricsAccessProjectionDisposition.Applied;
            }
        }

        dbContext.ListingAccessInboxMessages.Add(ToInboxRow(
            change,
            disposition,
            receivedAtUtc));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ListingMetricsAccessProjectionResult(resultProjection, disposition);
    }

    private async Task AcquireOwnerLocksAsync(
        ListingMetricsAccessProjectionChange change,
        CancellationToken cancellationToken)
    {
        var messageIdentity = change.MessageId.ToString("D");
        var grantIdentity = change.Projection.GrantId.ToString("D");
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({messageIdentity}, 3));",
            cancellationToken);
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({grantIdentity}, 4));",
            cancellationToken);
    }

    private static ListingMetricsAccessProjection RestoreProjection(
        AnalyticsListingAccessGrantProjectionRow row)
    {
        try
        {
            var projection = ListingMetricsAccessProjection.Create(
                row.GrantId,
                row.ListingId,
                row.ActorId,
                row.CanViewAnalytics,
                row.GrantedAtUtc,
                row.ExpiresAtUtc,
                row.RevokedAtUtc,
                row.SourceAggregateRevision,
                row.SourcePayloadDigest,
                row.ChangedAtUtc);
            if (!string.Equals(
                    projection.ProjectionDigest,
                    row.ProjectionDigest,
                    StringComparison.Ordinal))
            {
                throw PersistenceCorruption(
                    "ANALYTICS_ACCESS_PROJECTION_DIGEST_CORRUPT",
                    $"Stored grant projection '{row.GrantId:D}' does not match its projection digest.",
                    "Stop owner-report reads and rebuild the exact Catalog access-grant stream.");
            }

            return projection;
        }
        catch (AnalyticsDomainException exception)
        {
            throw PersistenceCorruption(
                "ANALYTICS_ACCESS_PROJECTION_ROW_CORRUPT",
                $"Stored grant projection '{row.GrantId:D}' violates the Analytics owner contract: {exception.Message}",
                "Stop owner-report reads and rebuild the exact Catalog access-grant stream.",
                exception);
        }
    }

    private static AnalyticsListingAccessGrantProjectionRow ToRow(
        ListingMetricsAccessProjection projection,
        string projectionDigest) =>
        new()
        {
            GrantId = projection.GrantId,
            ListingId = projection.ListingId,
            ActorId = projection.ActorId,
            CanViewAnalytics = projection.CanViewAnalytics,
            GrantedAtUtc = projection.GrantedAtUtc,
            ExpiresAtUtc = projection.ExpiresAtUtc,
            RevokedAtUtc = projection.RevokedAtUtc,
            SourceAggregateRevision = projection.SourceAggregateRevision,
            SourcePayloadDigest = projection.SourcePayloadDigest,
            ProjectionDigest = projectionDigest,
            ChangedAtUtc = projection.ChangedAtUtc,
        };

    private static void Apply(
        AnalyticsListingAccessGrantProjectionRow row,
        ListingMetricsAccessProjection projection,
        string projectionDigest)
    {
        row.CanViewAnalytics = projection.CanViewAnalytics;
        row.RevokedAtUtc = projection.RevokedAtUtc;
        row.SourceAggregateRevision = projection.SourceAggregateRevision;
        row.SourcePayloadDigest = projection.SourcePayloadDigest;
        row.ProjectionDigest = projectionDigest;
        row.ChangedAtUtc = projection.ChangedAtUtc;
    }

    private static AnalyticsListingAccessGrantInboxRow ToInboxRow(
        ListingMetricsAccessProjectionChange change,
        ListingMetricsAccessProjectionDisposition disposition,
        DateTimeOffset receivedAtUtc) =>
        new()
        {
            MessageId = change.MessageId,
            GrantId = change.Projection.GrantId,
            ListingId = change.Projection.ListingId,
            ActorId = change.Projection.ActorId,
            RoutingKey = change.RoutingKey,
            ContractIdentity = change.ContractIdentity,
            PayloadDigest = change.PayloadDigest,
            SourceAggregateRevision = change.Projection.SourceAggregateRevision,
            ReceivedAtUtc = receivedAtUtc,
            CorrelationId = change.CorrelationId,
            CausationId = change.CausationId,
            Disposition = (int)disposition,
            ResultProjectionDigest = change.ProjectionDigest,
            ProcessedAtUtc = receivedAtUtc,
        };

    private static void EnsureSameInbox(
        AnalyticsListingAccessGrantInboxRow persisted,
        ListingMetricsAccessProjectionChange incoming)
    {
        if (persisted.GrantId == incoming.Projection.GrantId &&
            persisted.ListingId == incoming.Projection.ListingId &&
            persisted.ActorId == incoming.Projection.ActorId &&
            string.Equals(persisted.RoutingKey, incoming.RoutingKey, StringComparison.Ordinal) &&
            string.Equals(persisted.ContractIdentity, incoming.ContractIdentity, StringComparison.Ordinal) &&
            string.Equals(persisted.PayloadDigest, incoming.PayloadDigest, StringComparison.Ordinal) &&
            persisted.SourceAggregateRevision == incoming.Projection.SourceAggregateRevision &&
            string.Equals(persisted.CorrelationId, incoming.CorrelationId, StringComparison.Ordinal) &&
            persisted.CausationId == incoming.CausationId &&
            string.Equals(
                persisted.ResultProjectionDigest,
                incoming.ProjectionDigest,
                StringComparison.Ordinal) &&
            Enum.IsDefined((ListingMetricsAccessProjectionDisposition)persisted.Disposition))
        {
            return;
        }

        throw Failure(
            "ANALYTICS_ACCESS_INBOX_MESSAGE_CONFLICT",
            409,
            $"Catalog access event '{incoming.MessageId:D}' is already stored with different metadata or payload identity.",
            "Stop Catalog access event consumption and inspect the conflicting broker deliveries.",
            incoming);
    }

    private static void EnsureImmutableIdentity(
        ListingMetricsAccessProjection current,
        ListingMetricsAccessProjection incoming,
        ListingMetricsAccessProjectionChange change)
    {
        if (current.GrantId == incoming.GrantId &&
            current.ListingId == incoming.ListingId &&
            current.ActorId == incoming.ActorId &&
            current.GrantedAtUtc == incoming.GrantedAtUtc &&
            current.ExpiresAtUtc == incoming.ExpiresAtUtc)
        {
            return;
        }

        throw Failure(
            "ANALYTICS_ACCESS_GRANT_IDENTITY_CONFLICT",
            409,
            $"Catalog grant '{incoming.GrantId:D}' changed immutable listing, actor, or interval identity.",
            "Stop Catalog access event consumption and inspect the producer grant revisions.",
            change,
            current.SourceAggregateRevision);
    }

    private static void EnsureSameProjection(
        ListingMetricsAccessProjection persisted,
        string persistedProjectionDigest,
        ListingMetricsAccessProjection incoming,
        string incomingProjectionDigest,
        ListingMetricsAccessProjectionChange change)
    {
        if (persisted == incoming &&
            string.Equals(
                persistedProjectionDigest,
                incomingProjectionDigest,
                StringComparison.Ordinal))
        {
            return;
        }

        throw Failure(
            "ANALYTICS_ACCESS_REVISION_DIGEST_CONFLICT",
            409,
            $"Catalog grant revision '{incoming.SourceAggregateRevision}' is already projected with different content.",
            "Stop Catalog access event consumption and inspect the conflicting producer revisions.",
            change,
            persisted.SourceAggregateRevision);
    }

    private static AnalyticsCommandException RevisionGap(
        ListingMetricsAccessProjectionChange change,
        long currentRevision) =>
        Failure(
            "ANALYTICS_ACCESS_REVISION_GAP",
            503,
            "The Catalog listing access projection cannot apply a revision gap.",
            "Replay Catalog listing access events beginning with the next expected grant revision.",
            change,
            currentRevision);

    private static AnalyticsCommandException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        ListingMetricsAccessProjectionChange change,
        long? currentRevision = null) =>
        new(
            "Analytics.AccessProjection",
            code,
            statusCode,
            message,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["messageId"] = change.MessageId,
                ["grantId"] = change.Projection.GrantId,
                ["listingId"] = change.Projection.ListingId,
                ["actorId"] = change.Projection.ActorId,
                ["currentRevision"] = currentRevision,
                ["incomingRevision"] = change.Projection.SourceAggregateRevision,
                ["incomingPayloadDigest"] = change.PayloadDigest,
                ["incomingProjectionDigest"] = change.ProjectionDigest,
            });

    private static AnalyticsCommandException PersistenceCorruption(
        string code,
        string message,
        string requiredAction,
        Exception? innerException = null)
    {
        var exception = new AnalyticsCommandException(
            "Analytics.Persistence",
            code,
            500,
            message,
            requiredAction);
        if (innerException is not null)
        {
            exception.Data[nameof(innerException)] = innerException;
        }

        return exception;
    }
}
