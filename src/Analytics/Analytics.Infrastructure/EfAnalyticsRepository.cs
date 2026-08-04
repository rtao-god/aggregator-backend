using System.Data;
using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Analytics.Infrastructure;

internal sealed class EfAnalyticsRepository(
    AnalyticsDbContext dbContext) :
    IAnalyticsEventStore,
    IPublicReadReferenceStore,
    IPublicReadReferenceProjectionWriter,
    IListingMetricsAccessProjectionWriter,
    IDailyListingMetricsStore,
    IListingMetricsAuthorizer
{
    private const string SemanticEventKeyConstraint =
        "ux_analytics_interaction_event_semantic_key";

    public async Task<InteractionEvent?> GetAsync(
        InteractionEventSemanticKey semanticKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(semanticKey);
        var row = await dbContext.InteractionEvents
            .AsNoTracking()
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.ClientEventId == semanticKey.ClientEventId &&
                    candidate.EventKind == (int)semanticKey.Kind,
                cancellationToken);
        return row is null
            ? null
            : await RestoreEventAsync(row, cancellationToken);
    }

    public async Task<InteractionEventRegistrationResult> RegisterAsync(
        InteractionEvent interactionEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(interactionEvent);
        dbContext.InteractionEvents.Add(ToRow(interactionEvent));
        foreach (var parameter in interactionEvent.CampaignParameters.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            dbContext.InteractionCampaignParameters.Add(new AnalyticsInteractionCampaignParameterRow
            {
                EventId = interactionEvent.Id,
                ParameterKey = parameter.Key,
                ParameterValue = parameter.Value,
            });
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new InteractionEventRegistrationResult(
                InteractionEventRegistrationState.Stored,
                interactionEvent);
        }
        catch (DbUpdateException exception) when (
            IsUniqueViolation(exception, SemanticEventKeyConstraint))
        {
            dbContext.ChangeTracker.Clear();
            var existing = await GetAsync(interactionEvent.SemanticKey, cancellationToken)
                ?? throw PersistenceFailure(
                    "ANALYTICS_EVENT_IDEMPOTENCY_ROW_MISSING",
                    "The semantic event key was reported as duplicated, but its persisted event cannot be read.",
                    "Stop interaction intake and restore the Analytics event table from a verified backup.");
            var state = string.Equals(
                existing.PayloadDigest,
                interactionEvent.PayloadDigest,
                StringComparison.Ordinal)
                ? InteractionEventRegistrationState.AlreadyApplied
                : InteractionEventRegistrationState.DigestConflict;
            return new InteractionEventRegistrationResult(state, existing);
        }
    }

    public async Task<PublicReadMembershipResult> ValidateMembershipAsync(
        Guid publicReadRevisionId,
        string catalogKey,
        Guid? listingId,
        CancellationToken cancellationToken)
    {
        AnalyticsDomainRules.RequireIdentifier(publicReadRevisionId, nameof(publicReadRevisionId));
        var normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        var reference = await dbContext.PublicReadReferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.PublicReadRevisionId == publicReadRevisionId,
                cancellationToken);
        if (reference is null)
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.UnknownRevision,
                ActualCatalogKey: null,
                ActualListingId: null);
        }

        if (!string.Equals(reference.CatalogKey, normalizedCatalogKey, StringComparison.Ordinal))
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.CatalogMismatch,
                reference.CatalogKey,
                ActualListingId: null);
        }

        if (listingId is null)
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.Known,
                reference.CatalogKey,
                ActualListingId: null);
        }

        var listingExists = await dbContext.PublicListingReferences
            .AsNoTracking()
            .AnyAsync(
                row =>
                    row.PublicReadRevisionId == publicReadRevisionId &&
                    row.ListingId == listingId.Value,
                cancellationToken);
        return listingExists
            ? new PublicReadMembershipResult(
                PublicReadMembershipState.Known,
                reference.CatalogKey,
                listingId)
            : new PublicReadMembershipResult(
                PublicReadMembershipState.ListingNotPublic,
                reference.CatalogKey,
                listingId);
    }

    public async Task ApplyAsync(
        PublicReadReferenceProjection projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        var existing = await ReadPublicReadProjectionAsync(
            projection.PublicReadRevisionId,
            cancellationToken);
        if (existing is not null)
        {
            EnsureSamePublicReadProjection(existing, projection);
            return;
        }

        dbContext.PublicReadReferences.Add(new AnalyticsPublicReadReferenceRow
        {
            PublicReadRevisionId = projection.PublicReadRevisionId,
            CatalogKey = projection.CatalogKey,
            BaseProjectionId = projection.BaseProjectionId,
            PromotionOverlayId = projection.PromotionOverlayId,
            SafetyOverlayId = projection.SafetyOverlayId,
            SourcePublicationId = projection.SourcePublicationId,
            PublicReadContentDigest = projection.PublicReadContentDigest,
            MembershipDigest = projection.MembershipDigest,
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

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.ChangeTracker.Clear();
            var raced = await ReadPublicReadProjectionAsync(
                projection.PublicReadRevisionId,
                cancellationToken)
                ?? throw PersistenceFailure(
                    "ANALYTICS_PUBLIC_REFERENCE_ROW_MISSING",
                    "A public-read projection identity collided, but its persisted projection cannot be read.",
                    "Stop Query event consumption and restore the Analytics access projection.");
            EnsureSamePublicReadProjection(raced, projection);
        }
    }

    public async Task ApplyAsync(
        ListingMetricsAccessProjection projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projection);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);
        var row = await dbContext.ListingAccessProjections
            .SingleOrDefaultAsync(
                candidate =>
                    candidate.ListingId == projection.ListingId &&
                    candidate.ActorId == projection.ActorId,
                cancellationToken);
        if (row is null)
        {
            if (projection.SourceAggregateRevision != 1)
            {
                throw AccessRevisionGap(projection, currentRevision: 0);
            }

            dbContext.ListingAccessProjections.Add(new AnalyticsListingAccessProjectionRow
            {
                ListingId = projection.ListingId,
                ActorId = projection.ActorId,
                CanViewAnalytics = projection.CanViewAnalytics,
                SourceAggregateRevision = projection.SourceAggregateRevision,
                SourcePayloadDigest = projection.SourcePayloadDigest,
                ChangedAtUtc = projection.ChangedAtUtc,
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (projection.SourceAggregateRevision == row.SourceAggregateRevision)
        {
            EnsureSameAccessProjection(row, projection);
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (projection.SourceAggregateRevision < row.SourceAggregateRevision)
        {
            throw new AnalyticsCommandException(
                "Analytics.AccessProjection",
                "ANALYTICS_ACCESS_REVISION_STALE",
                409,
                "A stale listing access revision cannot replace the Analytics authorization projection.",
                "Replay from the expected Catalog aggregate revision without resetting the current projection.",
                AccessRevisionContext(projection, row.SourceAggregateRevision));
        }

        if (projection.SourceAggregateRevision != row.SourceAggregateRevision + 1)
        {
            throw AccessRevisionGap(projection, row.SourceAggregateRevision);
        }

        row.CanViewAnalytics = projection.CanViewAnalytics;
        row.SourceAggregateRevision = projection.SourceAggregateRevision;
        row.SourcePayloadDigest = projection.SourcePayloadDigest;
        row.ChangedAtUtc = projection.ChangedAtUtc;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<DailyListingMetrics>> GetRangeAsync(
        string catalogKey,
        Guid listingId,
        DateOnly fromInclusive,
        DateOnly toExclusive,
        CancellationToken cancellationToken)
    {
        var normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
        var rows = await dbContext.DailyListingMetrics
            .AsNoTracking()
            .Where(row =>
                row.CatalogKey == normalizedCatalogKey &&
                row.ListingId == listingId &&
                row.MetricDate >= fromInclusive &&
                row.MetricDate < toExclusive)
            .OrderBy(row => row.MetricDate)
            .ToArrayAsync(cancellationToken);
        return rows.Select(RestoreMetrics).ToArray();
    }

    public async Task AuthorizeAsync(
        Guid actorId,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        AnalyticsDomainRules.RequireIdentifier(actorId, nameof(actorId));
        AnalyticsDomainRules.RequireIdentifier(listingId, nameof(listingId));
        var permission = await dbContext.ListingAccessProjections
            .AsNoTracking()
            .Where(row => row.ActorId == actorId && row.ListingId == listingId)
            .Select(row => (bool?)row.CanViewAnalytics)
            .SingleOrDefaultAsync(cancellationToken);
        if (permission is not true)
        {
            throw new AnalyticsCommandException(
                "Analytics.AccessProjection",
                "ANALYTICS_LISTING_METRICS_FORBIDDEN",
                403,
                "The actor has no local Analytics permission for this listing.",
                "Verify the Catalog listing access grant and consume its exact projection revision.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["actorId"] = actorId,
                    ["listingId"] = listingId,
                });
        }
    }

    private async Task<InteractionEvent> RestoreEventAsync(
        AnalyticsInteractionEventRow row,
        CancellationToken cancellationToken)
    {
        var parameterRows = await dbContext.InteractionCampaignParameters
            .AsNoTracking()
            .Where(parameter => parameter.EventId == row.Id)
            .OrderBy(parameter => parameter.ParameterKey)
            .ToArrayAsync(cancellationToken);
        var parameters = parameterRows.ToDictionary(
            parameter => parameter.ParameterKey,
            parameter => parameter.ParameterValue,
            StringComparer.Ordinal);
        try
        {
            var interactionEvent = InteractionEvent.CreateAccepted(
                row.Id,
                row.ClientEventId,
                (InteractionEventKind)row.EventKind,
                row.CatalogKey,
                row.ListingId,
                row.PublicReadRevisionId,
                row.OccurredAtUtc,
                row.ReceivedAtUtc,
                row.PageContext,
                PlacementContext.Create(
                    (PlacementExposureKind)row.PlacementExposureKind,
                    row.PlacementId,
                    row.PlacementScopeKey),
                (ReferrerClass)row.ReferrerClass,
                parameters,
                (ConsentMode)row.ConsentMode,
                row.PayloadDigest);
            var qualityState = (TrafficQualityState)row.QualityState;
            if (qualityState != TrafficQualityState.Accepted)
            {
                interactionEvent.ClassifyTraffic(qualityState);
            }

            return interactionEvent;
        }
        catch (AnalyticsDomainException exception)
        {
            throw PersistenceCorruption(
                "ANALYTICS_EVENT_ROW_CORRUPT",
                $"Persisted interaction event '{row.Id:D}' violates the Analytics domain contract: {exception.Message}",
                "Stop interaction reads and repair the persisted event from a verified source.",
                exception);
        }
    }

    private async Task<PublicReadReferenceProjection?> ReadPublicReadProjectionAsync(
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
        try
        {
            return PublicReadReferenceProjection.Create(
                row.PublicReadRevisionId,
                row.CatalogKey,
                row.BaseProjectionId,
                row.PromotionOverlayId,
                row.SafetyOverlayId,
                row.SourcePublicationId,
                row.PublicReadContentDigest,
                row.MembershipDigest,
                row.ActivatedAtUtc,
                listingIds);
        }
        catch (AnalyticsDomainException exception)
        {
            throw PersistenceCorruption(
                "ANALYTICS_PUBLIC_REFERENCE_CORRUPT",
                $"Persisted public-read reference '{row.PublicReadRevisionId:D}' violates its owner contract: {exception.Message}",
                "Stop Query event consumption and rebuild the Analytics public-reference projection.",
                exception);
        }
    }

    private static AnalyticsInteractionEventRow ToRow(InteractionEvent interactionEvent) =>
        new()
        {
            Id = interactionEvent.Id,
            ClientEventId = interactionEvent.SemanticKey.ClientEventId,
            EventKind = (int)interactionEvent.SemanticKey.Kind,
            CatalogKey = interactionEvent.CatalogKey,
            ListingId = interactionEvent.ListingId,
            PublicReadRevisionId = interactionEvent.PublicReadRevisionId,
            OccurredAtUtc = interactionEvent.OccurredAtUtc,
            ReceivedAtUtc = interactionEvent.ReceivedAtUtc,
            PageContext = interactionEvent.PageContext,
            PlacementExposureKind = (int)interactionEvent.PlacementContext.ExposureKind,
            PlacementId = interactionEvent.PlacementContext.PlacementId,
            PlacementScopeKey = interactionEvent.PlacementContext.ScopeKey,
            ReferrerClass = (int)interactionEvent.ReferrerClass,
            ConsentMode = (int)interactionEvent.ConsentMode,
            QualityState = (int)interactionEvent.QualityState,
            PayloadDigest = interactionEvent.PayloadDigest,
        };

    private static DailyListingMetrics RestoreMetrics(AnalyticsDailyListingMetricRow row)
    {
        try
        {
            var readiness = (AggregateReadinessState)row.ReadinessState;
            return readiness switch
            {
                AggregateReadinessState.Complete => DailyListingMetrics.Complete(
                    row.MetricDate,
                    row.CatalogKey,
                    row.ListingId,
                    row.AggregationSourceDigest,
                    row.SourceReadRevisionCount,
                    InteractionCounts.Create(
                        RequireCount(row.OrganicImpressions, nameof(row.OrganicImpressions)),
                        RequireCount(row.SponsoredImpressions, nameof(row.SponsoredImpressions)),
                        RequireCount(row.ListingOpens, nameof(row.ListingOpens)),
                        RequireCount(row.WebsiteClicks, nameof(row.WebsiteClicks)),
                        RequireCount(row.PhoneClicks, nameof(row.PhoneClicks)),
                        RequireCount(row.WhatsAppClicks, nameof(row.WhatsAppClicks)),
                        RequireCount(row.EmailClicks, nameof(row.EmailClicks)),
                        RequireCount(row.MapClicks, nameof(row.MapClicks)),
                        RequireCount(row.ExternalProfileClicks, nameof(row.ExternalProfileClicks)))),
                AggregateReadinessState.Partial or
                AggregateReadinessState.Blocked or
                AggregateReadinessState.Rebuilding => DailyListingMetrics.Unavailable(
                    row.MetricDate,
                    row.CatalogKey,
                    row.ListingId,
                    row.AggregationSourceDigest,
                    row.SourceReadRevisionCount,
                    readiness,
                    row.UnavailableReason ?? throw new AnalyticsDomainException(
                        "ANALYTICS_AGGREGATE_REASON_REQUIRED",
                        "An incomplete persisted aggregate has no unavailable reason.")),
                _ => throw new AnalyticsDomainException(
                    "ANALYTICS_AGGREGATE_READINESS_INVALID",
                    $"Persisted aggregate readiness '{row.ReadinessState}' is unsupported."),
            };
        }
        catch (AnalyticsDomainException exception)
        {
            throw PersistenceCorruption(
                "ANALYTICS_DAILY_METRIC_ROW_CORRUPT",
                $"Persisted daily metric for listing '{row.ListingId:D}' and date '{row.MetricDate:yyyy-MM-dd}' violates its owner contract: {exception.Message}",
                "Stop metrics reads and rebuild the exact Analytics aggregate range.",
                exception);
        }
    }

    private static long RequireCount(long? value, string valueName) =>
        value ?? throw new AnalyticsDomainException(
            "ANALYTICS_AGGREGATE_COUNT_REQUIRED",
            $"Complete persisted aggregate is missing '{valueName}'.");

    private static void EnsureSamePublicReadProjection(
        PublicReadReferenceProjection persisted,
        PublicReadReferenceProjection incoming)
    {
        if (persisted == incoming ||
            (persisted.PublicReadRevisionId == incoming.PublicReadRevisionId &&
             string.Equals(persisted.CatalogKey, incoming.CatalogKey, StringComparison.Ordinal) &&
             persisted.BaseProjectionId == incoming.BaseProjectionId &&
             persisted.PromotionOverlayId == incoming.PromotionOverlayId &&
             persisted.SafetyOverlayId == incoming.SafetyOverlayId &&
             persisted.SourcePublicationId == incoming.SourcePublicationId &&
             string.Equals(
                 persisted.PublicReadContentDigest,
                 incoming.PublicReadContentDigest,
                 StringComparison.Ordinal) &&
             string.Equals(persisted.MembershipDigest, incoming.MembershipDigest, StringComparison.Ordinal) &&
             persisted.ActivatedAtUtc == incoming.ActivatedAtUtc &&
             persisted.PublicListingIds.SequenceEqual(incoming.PublicListingIds)))
        {
            return;
        }

        throw new AnalyticsCommandException(
            "Analytics.PublicReference",
            "ANALYTICS_PUBLIC_REFERENCE_DIGEST_CONFLICT",
            409,
            "The public-read revision identity is already projected with different content or membership.",
            "Stop Query event consumption and inspect the producer event and Analytics projection row.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["publicReadRevisionId"] = incoming.PublicReadRevisionId,
                ["persistedContentDigest"] = persisted.PublicReadContentDigest,
                ["incomingContentDigest"] = incoming.PublicReadContentDigest,
                ["persistedMembershipDigest"] = persisted.MembershipDigest,
                ["incomingMembershipDigest"] = incoming.MembershipDigest,
            });
    }

    private static void EnsureSameAccessProjection(
        AnalyticsListingAccessProjectionRow persisted,
        ListingMetricsAccessProjection incoming)
    {
        if (persisted.CanViewAnalytics == incoming.CanViewAnalytics &&
            string.Equals(
                persisted.SourcePayloadDigest,
                incoming.SourcePayloadDigest,
                StringComparison.Ordinal) &&
            persisted.ChangedAtUtc == incoming.ChangedAtUtc)
        {
            return;
        }

        throw new AnalyticsCommandException(
            "Analytics.AccessProjection",
            "ANALYTICS_ACCESS_REVISION_DIGEST_CONFLICT",
            409,
            "The listing access revision is already projected with a different payload.",
            "Stop Catalog access event consumption and inspect the conflicting producer messages.",
            AccessRevisionContext(incoming, persisted.SourceAggregateRevision));
    }

    private static AnalyticsCommandException AccessRevisionGap(
        ListingMetricsAccessProjection incoming,
        long currentRevision) =>
        new(
            "Analytics.AccessProjection",
            "ANALYTICS_ACCESS_REVISION_GAP",
            409,
            "The listing access projection cannot apply a revision gap.",
            "Replay Catalog listing access events beginning with the next expected aggregate revision.",
            AccessRevisionContext(incoming, currentRevision));

    private static IReadOnlyDictionary<string, object?> AccessRevisionContext(
        ListingMetricsAccessProjection incoming,
        long currentRevision) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["listingId"] = incoming.ListingId,
            ["actorId"] = incoming.ActorId,
            ["currentRevision"] = currentRevision,
            ["incomingRevision"] = incoming.SourceAggregateRevision,
            ["incomingPayloadDigest"] = incoming.SourcePayloadDigest,
        };

    private static bool IsUniqueViolation(
        DbUpdateException exception,
        string? constraintName = null) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        } postgresException &&
        (constraintName is null ||
         string.Equals(postgresException.ConstraintName, constraintName, StringComparison.Ordinal));

    private static AnalyticsCommandException PersistenceFailure(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Analytics.Persistence",
            code,
            500,
            message,
            requiredAction);

    private static AnalyticsCommandException PersistenceCorruption(
        string code,
        string message,
        string requiredAction,
        Exception innerException) =>
        new AnalyticsPersistenceCorruptionException(
            code,
            message,
            requiredAction,
            innerException);

    private sealed class AnalyticsPersistenceCorruptionException : AnalyticsCommandException
    {
        public AnalyticsPersistenceCorruptionException(
            string code,
            string message,
            string requiredAction,
            Exception innerException)
            : base(
                "Analytics.Persistence",
                code,
                500,
                message,
                requiredAction)
        {
            Data[nameof(innerException)] = innerException;
        }
    }
}
