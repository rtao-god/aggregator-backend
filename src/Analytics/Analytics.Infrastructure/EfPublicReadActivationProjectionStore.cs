using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;

namespace Aggregator.Analytics.Infrastructure;

/// <summary>Atomically persists Query activation inbox state and the Analytics public-reference projection.</summary>
public sealed class EfPublicReadActivationProjectionStore(AnalyticsDbContext dbContext)
    : IPublicReadActivationProjectionStore
{
    public async Task<PublicReadActivationProjectionResult> ApplyAsync(
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(inboxMessage);
        if (projection.ActivationRevision != inboxMessage.ActivationRevision)
        {
            throw Failure(
                "ANALYTICS_PUBLIC_ACTIVATION_REVISION_MISMATCH",
                409,
                "The Query projection revision and inbox activation revision do not match.",
                "Stop Query event consumption and inspect the producer envelope and payload identities.",
                projection,
                inboxMessage);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.Serializable,
            cancellationToken);
        await AcquireOwnerLocksAsync(projection, inboxMessage, cancellationToken);

        var existingInbox = await dbContext.PublicReadInboxMessages
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.MessageId == inboxMessage.EventId,
                cancellationToken);
        if (existingInbox is not null)
        {
            EnsureSameInbox(existingInbox, projection, inboxMessage);
            var replayedProjection = await RequireProjectionAsync(
                existingInbox.PublicReadRevisionId,
                cancellationToken);
            EnsureSameProjection(replayedProjection, projection, inboxMessage);
            await transaction.CommitAsync(cancellationToken);
            return new PublicReadActivationProjectionResult(
                replayedProjection,
                PublicReadActivationDisposition.Replayed);
        }

        var checkpoint = await dbContext.PublicReadActivationCheckpoints
            .SingleOrDefaultAsync(
                row => row.CatalogKey == projection.CatalogKey,
                cancellationToken);
        var existingProjection = await ReadProjectionAsync(
            projection.PublicReadRevisionId,
            cancellationToken);

        if (checkpoint is not null)
        {
            var checkpointProjection = await RequireProjectionAsync(
                checkpoint.PublicReadRevisionId,
                cancellationToken);
            EnsureCheckpoint(checkpoint, checkpointProjection, projection, inboxMessage);
        }

        if (checkpoint is null)
        {
            if (projection.ActivationRevision != 1)
            {
                throw RevisionGap(projection, inboxMessage, currentRevision: 0);
            }

            if (existingProjection is not null)
            {
                throw Failure(
                    "ANALYTICS_PUBLIC_ACTIVATION_CHECKPOINT_MISSING",
                    500,
                    "A public-read projection exists without its Analytics activation checkpoint.",
                    "Stop interaction intake and rebuild the Analytics public-reference projection from Query events.",
                    projection,
                    inboxMessage);
            }

            AddProjection(projection);
            dbContext.PublicReadActivationCheckpoints.Add(ToCheckpointRow(
                projection,
                inboxMessage.ReceivedAtUtc));
            dbContext.PublicReadInboxMessages.Add(ToInboxRow(
                projection,
                inboxMessage,
                PublicReadActivationDisposition.Applied));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PublicReadActivationProjectionResult(
                projection,
                PublicReadActivationDisposition.Applied);
        }

        var nextExpectedRevision = NextActivationRevision(
            checkpoint.ActivationRevision,
            projection,
            inboxMessage);
        if (projection.ActivationRevision > nextExpectedRevision)
        {
            throw RevisionGap(projection, inboxMessage, checkpoint.ActivationRevision);
        }

        if (projection.ActivationRevision == nextExpectedRevision)
        {
            if (existingProjection is not null)
            {
                throw Failure(
                    "ANALYTICS_PUBLIC_REVISION_ID_REUSED",
                    409,
                    $"Public-read revision '{projection.PublicReadRevisionId:D}' already exists at another activation position.",
                    "Stop Query event consumption and inspect the conflicting public-read revision identity.",
                    projection,
                    inboxMessage,
                    checkpoint.ActivationRevision);
            }

            AddProjection(projection);
            ApplyCheckpoint(checkpoint, projection, inboxMessage.ReceivedAtUtc);
            dbContext.PublicReadInboxMessages.Add(ToInboxRow(
                projection,
                inboxMessage,
                PublicReadActivationDisposition.Applied));
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PublicReadActivationProjectionResult(
                projection,
                PublicReadActivationDisposition.Applied);
        }

        if (projection.ActivationRevision == checkpoint.ActivationRevision &&
            checkpoint.PublicReadRevisionId != projection.PublicReadRevisionId)
        {
            throw Failure(
                "ANALYTICS_PUBLIC_ACTIVATION_REVISION_CONFLICT",
                409,
                $"Activation revision '{projection.ActivationRevision}' is already bound to another public-read revision.",
                "Stop Query event consumption and inspect the conflicting activation identity.",
                projection,
                inboxMessage,
                checkpoint.ActivationRevision);
        }

        if (existingProjection is null)
        {
            throw Failure(
                "ANALYTICS_PUBLIC_ACTIVATION_HISTORY_MISSING",
                503,
                $"Activation revision '{projection.ActivationRevision}' is behind the Analytics checkpoint but its exact public-read projection is absent.",
                "Replay or rebuild the complete Query public-read activation stream before accepting dependent interactions.",
                projection,
                inboxMessage,
                checkpoint.ActivationRevision);
        }

        EnsureSameProjection(existingProjection, projection, inboxMessage);
        var disposition = projection.ActivationRevision == checkpoint.ActivationRevision
            ? PublicReadActivationDisposition.Replayed
            : PublicReadActivationDisposition.IgnoredStale;
        dbContext.PublicReadInboxMessages.Add(ToInboxRow(
            existingProjection,
            inboxMessage,
            disposition));
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PublicReadActivationProjectionResult(existingProjection, disposition);
    }

    private async Task AcquireOwnerLocksAsync(
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage inboxMessage,
        CancellationToken cancellationToken)
    {
        var messageLockIdentity = inboxMessage.EventId.ToString("D");
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({messageLockIdentity}, 1));",
            cancellationToken);
        _ = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({projection.CatalogKey}, 2));",
            cancellationToken);
    }

    private void AddProjection(PublicReadReferenceProjection projection)
    {
        dbContext.PublicReadReferences.Add(new AnalyticsPublicReadReferenceRow
        {
            PublicReadRevisionId = projection.PublicReadRevisionId,
            CatalogKey = projection.CatalogKey,
            ActivationRevision = projection.ActivationRevision,
            BaseProjectionId = projection.BaseProjectionId,
            PromotionOverlayId = projection.PromotionOverlayId,
            SafetyOverlayId = projection.SafetyOverlayId,
            SourcePublicationId = projection.SourcePublicationId,
            PublicReadContentDigest = projection.PublicReadContentDigest,
            MembershipDigest = projection.MembershipDigest,
            ProjectionDigest = projection.ProjectionDigest,
            ActivatedAtUtc = projection.ActivatedAtUtc,
        });
        foreach (var listingId in projection.PublicListingIds)
        {
            dbContext.PublicListingReferences.Add(new AnalyticsPublicListingReferenceRow
            {
                PublicReadRevisionId = projection.PublicReadRevisionId,
                ListingId = listingId,
            });
        }

        foreach (var placement in projection.SponsoredPlacements)
        {
            dbContext.PublicSponsoredPlacementReferences.Add(
                new AnalyticsPublicSponsoredPlacementReferenceRow
                {
                    PublicReadRevisionId = projection.PublicReadRevisionId,
                    PlacementId = placement.PlacementId,
                    ListingId = placement.ListingId,
                    ScopeType = (int)placement.ScopeType,
                    ScopeKey = placement.ScopeKey,
                    StartsAtUtc = placement.StartsAtUtc,
                    HardExpiryAtUtc = placement.HardExpiryAtUtc,
                });
        }
    }

    private async Task<PublicReadReferenceProjection?> ReadProjectionAsync(
        Guid publicReadRevisionId,
        CancellationToken cancellationToken)
    {
        var row = await dbContext.PublicReadReferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.PublicReadRevisionId == publicReadRevisionId,
                cancellationToken);
        if (row is null)
        {
            return null;
        }

        var listingIds = await dbContext.PublicListingReferences
            .AsNoTracking()
            .Where(candidate => candidate.PublicReadRevisionId == publicReadRevisionId)
            .OrderBy(candidate => candidate.ListingId)
            .Select(candidate => candidate.ListingId)
            .ToArrayAsync(cancellationToken);
        var placementRows = await dbContext.PublicSponsoredPlacementReferences
            .AsNoTracking()
            .Where(candidate => candidate.PublicReadRevisionId == publicReadRevisionId)
            .OrderBy(candidate => candidate.PlacementId)
            .ToArrayAsync(cancellationToken);
        try
        {
            var projection = PublicReadReferenceProjection.Create(
                row.PublicReadRevisionId,
                row.CatalogKey,
                row.ActivationRevision,
                row.BaseProjectionId,
                row.PromotionOverlayId,
                row.SafetyOverlayId,
                row.SourcePublicationId,
                row.PublicReadContentDigest,
                row.MembershipDigest,
                row.ActivatedAtUtc,
                listingIds,
                placementRows.Select(row => PublicReadSponsoredPlacementProjection.Create(
                    row.PlacementId,
                    row.ListingId,
                    (PublicReadSponsoredPlacementScope)row.ScopeType,
                    row.ScopeKey,
                    row.StartsAtUtc,
                    row.HardExpiryAtUtc)));
            if (!string.Equals(
                    projection.ProjectionDigest,
                    row.ProjectionDigest,
                    StringComparison.Ordinal))
            {
                throw PersistenceCorruption(
                    "ANALYTICS_PUBLIC_REFERENCE_DIGEST_CORRUPT",
                    $"Stored public-read projection '{publicReadRevisionId:D}' does not match its projection digest.",
                    "Stop interaction intake and rebuild the Analytics public-reference projection from Query events.");
            }

            return projection;
        }
        catch (AnalyticsDomainException exception)
        {
            throw PersistenceCorruption(
                "ANALYTICS_PUBLIC_REFERENCE_ROW_CORRUPT",
                $"Stored public-read projection '{publicReadRevisionId:D}' violates the Analytics owner contract: {exception.Message}",
                "Stop interaction intake and rebuild the Analytics public-reference projection from Query events.");
        }
    }

    private async Task<PublicReadReferenceProjection> RequireProjectionAsync(
        Guid publicReadRevisionId,
        CancellationToken cancellationToken) =>
        await ReadProjectionAsync(publicReadRevisionId, cancellationToken)
        ?? throw PersistenceCorruption(
            "ANALYTICS_PUBLIC_INBOX_RESULT_MISSING",
            $"Analytics state references missing public-read projection '{publicReadRevisionId:D}'.",
            "Stop interaction intake and rebuild the Analytics public-reference projection from Query events.");

    private static void EnsureCheckpoint(
        AnalyticsPublicReadActivationCheckpointRow checkpoint,
        PublicReadReferenceProjection checkpointProjection,
        PublicReadReferenceProjection incomingProjection,
        PublicReadActivationInboxMessage inboxMessage)
    {
        if (checkpointProjection.ActivationRevision == checkpoint.ActivationRevision &&
            string.Equals(
                checkpointProjection.CatalogKey,
                incomingProjection.CatalogKey,
                StringComparison.Ordinal) &&
            string.Equals(
                checkpointProjection.ProjectionDigest,
                checkpoint.ProjectionDigest,
                StringComparison.Ordinal))
        {
            return;
        }

        throw Failure(
            "ANALYTICS_PUBLIC_CHECKPOINT_CORRUPT",
            500,
            $"Public-read checkpoint for catalog '{incomingProjection.CatalogKey}' does not match its referenced projection.",
            "Stop interaction intake and rebuild the Analytics public-reference projection from Query events.",
            incomingProjection,
            inboxMessage,
            checkpoint.ActivationRevision);
    }

    private static void EnsureSameInbox(
        AnalyticsInboxMessageRow persisted,
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage incoming)
    {
        if (string.Equals(persisted.CatalogKey, projection.CatalogKey, StringComparison.Ordinal) &&
            string.Equals(persisted.RoutingKey, incoming.RoutingKey, StringComparison.Ordinal) &&
            string.Equals(persisted.ContractIdentity, incoming.ContractIdentity, StringComparison.Ordinal) &&
            string.Equals(persisted.PayloadDigest, incoming.PayloadDigest, StringComparison.Ordinal) &&
            persisted.ActivationRevision == incoming.ActivationRevision &&
            persisted.PublicReadRevisionId == projection.PublicReadRevisionId &&
            string.Equals(persisted.CorrelationId, incoming.CorrelationId, StringComparison.Ordinal) &&
            string.Equals(persisted.ResultProjectionDigest, projection.ProjectionDigest, StringComparison.Ordinal) &&
            Enum.IsDefined((PublicReadActivationDisposition)persisted.Disposition))
        {
            return;
        }

        throw Failure(
            "ANALYTICS_INBOX_MESSAGE_CORRUPT",
            409,
            $"Query event '{incoming.EventId:D}' is already stored with different metadata, payload, or result identity.",
            "Stop Query event consumption and inspect the conflicting broker deliveries.",
            projection,
            incoming,
            persisted.ActivationRevision);
    }

    private static void EnsureSameProjection(
        PublicReadReferenceProjection persisted,
        PublicReadReferenceProjection incoming,
        PublicReadActivationInboxMessage inboxMessage)
    {
        if (persisted.PublicReadRevisionId == incoming.PublicReadRevisionId &&
            persisted.ActivationRevision == incoming.ActivationRevision &&
            string.Equals(persisted.CatalogKey, incoming.CatalogKey, StringComparison.Ordinal) &&
            string.Equals(persisted.ProjectionDigest, incoming.ProjectionDigest, StringComparison.Ordinal))
        {
            return;
        }

        throw Failure(
            "ANALYTICS_PUBLIC_REVISION_DIGEST_CONFLICT",
            409,
            $"Public-read revision '{incoming.PublicReadRevisionId:D}' is already projected with different content or activation identity.",
            "Stop Query event consumption and inspect the conflicting public-read activation payloads.",
            incoming,
            inboxMessage,
            persisted.ActivationRevision);
    }

    private static AnalyticsPublicReadActivationCheckpointRow ToCheckpointRow(
        PublicReadReferenceProjection projection,
        DateTimeOffset updatedAtUtc) =>
        new()
        {
            CatalogKey = projection.CatalogKey,
            ActivationRevision = projection.ActivationRevision,
            PublicReadRevisionId = projection.PublicReadRevisionId,
            ProjectionDigest = projection.ProjectionDigest,
            UpdatedAtUtc = updatedAtUtc,
        };

    private static void ApplyCheckpoint(
        AnalyticsPublicReadActivationCheckpointRow checkpoint,
        PublicReadReferenceProjection projection,
        DateTimeOffset updatedAtUtc)
    {
        checkpoint.ActivationRevision = projection.ActivationRevision;
        checkpoint.PublicReadRevisionId = projection.PublicReadRevisionId;
        checkpoint.ProjectionDigest = projection.ProjectionDigest;
        checkpoint.UpdatedAtUtc = updatedAtUtc;
    }

    private static AnalyticsInboxMessageRow ToInboxRow(
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage inboxMessage,
        PublicReadActivationDisposition disposition) =>
        new()
        {
            MessageId = inboxMessage.EventId,
            CatalogKey = projection.CatalogKey,
            RoutingKey = inboxMessage.RoutingKey,
            ContractIdentity = inboxMessage.ContractIdentity,
            PayloadDigest = inboxMessage.PayloadDigest,
            ActivationRevision = inboxMessage.ActivationRevision,
            PublicReadRevisionId = projection.PublicReadRevisionId,
            ReceivedAtUtc = inboxMessage.ReceivedAtUtc,
            CorrelationId = inboxMessage.CorrelationId,
            Disposition = (int)disposition,
            ResultProjectionDigest = projection.ProjectionDigest,
            ProcessedAtUtc = inboxMessage.ReceivedAtUtc,
        };

    private static long NextActivationRevision(
        long currentRevision,
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage inboxMessage)
    {
        if (currentRevision == long.MaxValue)
        {
            throw Failure(
                "ANALYTICS_PUBLIC_ACTIVATION_REVISION_EXHAUSTED",
                500,
                "The Analytics public-read activation revision has reached its numeric limit.",
                "Stop Query event consumption and migrate the activation revision owner before accepting another revision.",
                projection,
                inboxMessage,
                currentRevision);
        }

        return currentRevision + 1;
    }

    private static AnalyticsCommandException RevisionGap(
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage inboxMessage,
        long currentRevision) =>
        Failure(
            "ANALYTICS_PUBLIC_ACTIVATION_REVISION_GAP",
            503,
            $"Analytics expected public-read activation revision '{NextActivationRevision(currentRevision, projection, inboxMessage)}' but received '{projection.ActivationRevision}'.",
            "Replay Query public-read activation events beginning with the next expected revision.",
            projection,
            inboxMessage,
            currentRevision);

    private static AnalyticsCommandException Failure(
        string code,
        int statusCode,
        string message,
        string requiredAction,
        PublicReadReferenceProjection projection,
        PublicReadActivationInboxMessage? inboxMessage,
        long? currentRevision = null) =>
        new(
            "Analytics.PublicReference",
            code,
            statusCode,
            message,
            requiredAction,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["catalogKey"] = projection.CatalogKey,
                ["publicReadRevisionId"] = projection.PublicReadRevisionId,
                ["incomingActivationRevision"] = projection.ActivationRevision,
                ["currentActivationRevision"] = currentRevision,
                ["messageId"] = inboxMessage?.EventId,
                ["incomingPayloadDigest"] = inboxMessage?.PayloadDigest,
                ["incomingProjectionDigest"] = projection.ProjectionDigest,
            });

    private static AnalyticsCommandException PersistenceCorruption(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Analytics.Persistence",
            code,
            500,
            message,
            requiredAction);
}
