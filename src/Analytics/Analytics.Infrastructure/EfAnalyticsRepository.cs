using Aggregator.Analytics.Application;
using Aggregator.Analytics.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Analytics.Infrastructure;

internal sealed class EfAnalyticsRepository(
    AnalyticsDbContext dbContext) :
    IAnalyticsEventStore,
    IPublicReadReferenceStore,
    IDailyListingMetricsStore
{
    private const string SemanticEventKeyConstraint =
        "ux_analytics_interaction_event_semantic_key";

    public async Task<InteractionEventReceipt?> GetAsync(
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
            : ToReceipt(row);
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
                InteractionEventReceipt.FromEvent(interactionEvent));
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

    public async Task<PublicReadMembershipResult> ValidateInteractionAsync(
        Guid publicReadRevisionId,
        string catalogKey,
        Guid? listingId,
        PlacementContext placementContext,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        AnalyticsDomainRules.RequireIdentifier(publicReadRevisionId, nameof(publicReadRevisionId));
        var normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        ArgumentNullException.ThrowIfNull(placementContext);
        AnalyticsDomainRules.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
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
            return placementContext.ExposureKind == PlacementExposureKind.Sponsored
                ? new PublicReadMembershipResult(
                    PublicReadMembershipState.ListingRequired,
                    reference.CatalogKey,
                    ActualListingId: null,
                    placementContext.PlacementId)
                : new PublicReadMembershipResult(
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
        if (!listingExists)
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.ListingNotPublic,
                reference.CatalogKey,
                listingId);
        }

        if (placementContext.ExposureKind != PlacementExposureKind.Sponsored)
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.Known,
                reference.CatalogKey,
                listingId);
        }

        var placementId = placementContext.PlacementId
            ?? throw PersistenceFailure(
                "ANALYTICS_SPONSORED_PLACEMENT_ID_REQUIRED",
                "A sponsored Analytics placement context reached persistence without a placement ID.",
                "Stop interaction intake and repair the Analytics domain-to-persistence call path.");
        var placement = await dbContext.PublicSponsoredPlacementReferences
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row =>
                    row.PublicReadRevisionId == publicReadRevisionId &&
                    row.PlacementId == placementId,
                cancellationToken);
        if (placement is null)
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.SponsoredPlacementNotPublic,
                reference.CatalogKey,
                listingId,
                placementId);
        }

        if (placement.ListingId != listingId.Value)
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.SponsoredPlacementListingMismatch,
                reference.CatalogKey,
                listingId,
                placement.PlacementId,
                placement.ListingId,
                placement.ScopeKey);
        }

        if (placementContext.ScopeKey is not null &&
            !string.Equals(placement.ScopeKey, placementContext.ScopeKey, StringComparison.Ordinal))
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.SponsoredPlacementScopeMismatch,
                reference.CatalogKey,
                listingId,
                placement.PlacementId,
                placement.ListingId,
                placement.ScopeKey);
        }

        if (occurredAtUtc < placement.StartsAtUtc || occurredAtUtc >= placement.HardExpiryAtUtc)
        {
            return new PublicReadMembershipResult(
                PublicReadMembershipState.SponsoredPlacementInactive,
                reference.CatalogKey,
                listingId,
                placement.PlacementId,
                placement.ListingId,
                placement.ScopeKey);
        }

        return new PublicReadMembershipResult(
            PublicReadMembershipState.Known,
            reference.CatalogKey,
            listingId,
            placement.PlacementId,
            placement.ListingId,
            placement.ScopeKey);
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

    private static InteractionEventReceipt ToReceipt(AnalyticsInteractionEventRow row)
    {
        try
        {
            return InteractionEventReceipt.Create(
                row.Id,
                InteractionEventSemanticKey.Create(
                    row.ClientEventId,
                    (InteractionEventKind)row.EventKind),
                row.PayloadDigest,
                (TrafficQualityState)row.QualityState,
                row.ReceivedAtUtc,
                row.PublicReadRevisionId,
                row.ListingId,
                (InteractionEventRetentionState)row.RetentionState,
                row.RetainedAtUtc,
                row.RetentionOperationId);
        }
        catch (AnalyticsDomainException exception)
        {
            throw PersistenceCorruption(
                "ANALYTICS_EVENT_RECEIPT_CORRUPT",
                $"Persisted interaction event '{row.Id:D}' cannot produce an exact idempotency receipt: {exception.Message}",
                "Stop interaction intake and repair the persisted event/retention evidence from a verified source.",
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
            RetentionState = (int)InteractionEventRetentionState.Raw,
            RetainedAtUtc = null,
            RetentionOperationId = null,
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

    private static AnalyticsPersistenceCorruptionException PersistenceCorruption(
        string code,
        string message,
        string requiredAction,
        Exception innerException) =>
        new(
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
