using System.Collections.ObjectModel;

namespace Aggregator.Analytics.Domain;

public enum InteractionEventKind
{
    SearchResultsViewed = 1,
    ListingImpression = 2,
    ListingOpened = 3,
    WebsiteClicked = 4,
    PhoneClicked = 5,
    WhatsAppClicked = 6,
    EmailClicked = 7,
    MapClicked = 8,
    ExternalProfileClicked = 9,
    ClaimStarted = 10,
    ClaimSubmitted = 11,
}

public enum ReferrerClass
{
    Direct = 1,
    Internal = 2,
    Search = 3,
    Social = 4,
    Campaign = 5,
    Other = 6,
    Unknown = 7,
}

public enum ConsentMode
{
    EssentialOnly = 1,
    AnalyticsAllowed = 2,
}

public enum PlacementExposureKind
{
    Organic = 1,
    Sponsored = 2,
    NotApplicable = 3,
}

public enum TrafficQualityState
{
    Accepted = 1,
    SuspectedBot = 2,
    KnownBot = 3,
    RateLimited = 4,
    Invalid = 5,
    Duplicate = 6,
}

public sealed record PlacementContext(
    PlacementExposureKind ExposureKind,
    Guid? PlacementId,
    string? ScopeKey)
{
    public static PlacementContext Create(
        PlacementExposureKind exposureKind,
        Guid? placementId,
        string? scopeKey)
    {
        if (!Enum.IsDefined(exposureKind))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PLACEMENT_EXPOSURE_INVALID",
                $"Placement exposure kind '{exposureKind}' is unsupported.");
        }

        if (exposureKind == PlacementExposureKind.Sponsored)
        {
            if (placementId is null || placementId == Guid.Empty)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_SPONSORED_PLACEMENT_REQUIRED",
                    "Sponsored interaction context requires a non-empty placement ID.");
            }
        }
        else if (placementId is not null)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_PLACEMENT_ID_FORBIDDEN",
                "Only sponsored interaction context can carry a placement ID.");
        }

        var normalizedScope = string.IsNullOrWhiteSpace(scopeKey)
            ? null
            : AnalyticsDomainRules.RequireKey(scopeKey, nameof(scopeKey), maximumLength: 200);
        return new PlacementContext(exposureKind, placementId, normalizedScope);
    }
}

public sealed record InteractionEventSemanticKey(Guid ClientEventId, InteractionEventKind Kind)
{
    public static InteractionEventSemanticKey Create(Guid clientEventId, InteractionEventKind kind)
    {
        AnalyticsDomainRules.RequireIdentifier(clientEventId, nameof(clientEventId));
        if (!Enum.IsDefined(kind))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_EVENT_KIND_INVALID",
                $"Interaction event kind '{kind}' is unsupported.");
        }

        return new InteractionEventSemanticKey(clientEventId, kind);
    }
}

public sealed class InteractionEvent
{
    private static readonly HashSet<string> CampaignParameterAllowlist =
        new(StringComparer.Ordinal)
        {
            "utm_source",
            "utm_medium",
            "utm_campaign",
            "utm_content",
            "utm_term",
        };

    private InteractionEvent(
        Guid id,
        InteractionEventSemanticKey semanticKey,
        string catalogKey,
        Guid? listingId,
        Guid publicReadRevisionId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc,
        string pageContext,
        PlacementContext placementContext,
        ReferrerClass referrerClass,
        IReadOnlyDictionary<string, string> campaignParameters,
        ConsentMode consentMode,
        TrafficQualityState qualityState,
        string payloadDigest)
    {
        Id = id;
        SemanticKey = semanticKey;
        CatalogKey = catalogKey;
        ListingId = listingId;
        PublicReadRevisionId = publicReadRevisionId;
        OccurredAtUtc = occurredAtUtc;
        ReceivedAtUtc = receivedAtUtc;
        PageContext = pageContext;
        PlacementContext = placementContext;
        ReferrerClass = referrerClass;
        CampaignParameters = campaignParameters;
        ConsentMode = consentMode;
        QualityState = qualityState;
        PayloadDigest = payloadDigest;
    }

    public Guid Id { get; }

    public InteractionEventSemanticKey SemanticKey { get; }

    public string CatalogKey { get; }

    public Guid? ListingId { get; }

    public Guid PublicReadRevisionId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset ReceivedAtUtc { get; }

    public string PageContext { get; }

    public PlacementContext PlacementContext { get; }

    public ReferrerClass ReferrerClass { get; }

    public IReadOnlyDictionary<string, string> CampaignParameters { get; }

    public ConsentMode ConsentMode { get; }

    public TrafficQualityState QualityState { get; private set; }

    public string PayloadDigest { get; }

    public static InteractionEvent CreateAccepted(
        Guid id,
        Guid clientEventId,
        InteractionEventKind kind,
        string catalogKey,
        Guid? listingId,
        Guid publicReadRevisionId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset receivedAtUtc,
        string pageContext,
        PlacementContext placementContext,
        ReferrerClass referrerClass,
        IReadOnlyDictionary<string, string> campaignParameters,
        ConsentMode consentMode,
        string payloadDigest)
    {
        AnalyticsDomainRules.RequireIdentifier(id, nameof(id));
        var semanticKey = InteractionEventSemanticKey.Create(clientEventId, kind);
        var normalizedCatalogKey = AnalyticsDomainRules.RequireKey(catalogKey, nameof(catalogKey));
        AnalyticsDomainRules.RequireIdentifier(publicReadRevisionId, nameof(publicReadRevisionId));
        AnalyticsDomainRules.RequireUtc(occurredAtUtc, nameof(occurredAtUtc));
        AnalyticsDomainRules.RequireUtc(receivedAtUtc, nameof(receivedAtUtc));
        ArgumentNullException.ThrowIfNull(placementContext);
        ArgumentNullException.ThrowIfNull(campaignParameters);
        if (RequiresListing(kind))
        {
            if (listingId is null || listingId == Guid.Empty)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_LISTING_REQUIRED",
                    $"Interaction kind '{kind}' requires a non-empty listing ID.");
            }
        }
        else if (listingId is not null)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_LISTING_FORBIDDEN",
                $"Interaction kind '{kind}' cannot carry a listing ID.");
        }

        if (occurredAtUtc < receivedAtUtc.AddDays(-7) ||
            occurredAtUtc > receivedAtUtc.AddMinutes(5))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_EVENT_TIME_OUT_OF_BOUNDS",
                "Interaction occurrence time is outside the accepted relation to server receive time.");
        }

        if (!Enum.IsDefined(referrerClass) || !Enum.IsDefined(consentMode))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_CONTEXT_ENUM_INVALID",
                "Interaction context contains an unsupported enum value.");
        }

        var normalizedPageContext = AnalyticsDomainRules.RequireKey(
            pageContext,
            nameof(pageContext),
            maximumLength: 120);
        var normalizedParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in campaignParameters)
        {
            if (!CampaignParameterAllowlist.Contains(parameter.Key))
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_CAMPAIGN_PARAMETER_FORBIDDEN",
                    $"Campaign parameter '{parameter.Key}' is not allowlisted.");
            }

            if (string.IsNullOrWhiteSpace(parameter.Value) || parameter.Value.Length > 200)
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_CAMPAIGN_PARAMETER_INVALID",
                    $"Campaign parameter '{parameter.Key}' has an invalid value.");
            }

            if (!normalizedParameters.TryAdd(parameter.Key, parameter.Value.Trim()))
            {
                throw new AnalyticsDomainException(
                    "ANALYTICS_CAMPAIGN_PARAMETER_DUPLICATE",
                    $"Campaign parameter '{parameter.Key}' is duplicated.");
            }
        }

        return new InteractionEvent(
            id,
            semanticKey,
            normalizedCatalogKey,
            listingId,
            publicReadRevisionId,
            occurredAtUtc,
            receivedAtUtc,
            normalizedPageContext,
            placementContext,
            referrerClass,
            new ReadOnlyDictionary<string, string>(normalizedParameters),
            consentMode,
            TrafficQualityState.Accepted,
            AnalyticsDomainRules.RequireDigest(payloadDigest, nameof(payloadDigest)));
    }

    public void ClassifyTraffic(TrafficQualityState qualityState)
    {
        if (qualityState is TrafficQualityState.Duplicate or TrafficQualityState.Invalid)
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_TRAFFIC_CLASSIFICATION_INVALID",
                "Duplicate and invalid are intake decisions, not classifications of an accepted event.");
        }

        if (!Enum.IsDefined(qualityState))
        {
            throw new AnalyticsDomainException(
                "ANALYTICS_TRAFFIC_CLASSIFICATION_INVALID",
                $"Traffic quality state '{qualityState}' is unsupported.");
        }

        QualityState = qualityState;
    }

    private static bool RequiresListing(InteractionEventKind kind) =>
        kind != InteractionEventKind.SearchResultsViewed;
}
