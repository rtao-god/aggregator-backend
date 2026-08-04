using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aggregator.Analytics.Contracts;
using Aggregator.Ingestion.Collector.Contracts;
using Aggregator.Promotion.Contracts;

namespace Aggregator.Acceptance.Runner;

public sealed class AcceptanceScenario
{
    private readonly AcceptanceOptions _options;
    private readonly HttpClient _identityClient;
    private readonly HttpClient _collectorClient;
    private readonly HttpClient _catalogControlClient;
    private readonly HttpClient _queryClient;
    private readonly HttpClient _analyticsClient;
    private readonly HttpClient _promotionClient;

    public AcceptanceScenario(AcceptanceOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _identityClient = CreateClient(options.IdentityBaseUrl);
        _collectorClient = CreateClient(options.CollectorBaseUrl);
        _catalogControlClient = CreateClient(options.CatalogControlBaseUrl);
        _queryClient = CreateClient(options.QueryBaseUrl);
        _analyticsClient = CreateClient(options.AnalyticsBaseUrl);
        _promotionClient = CreateClient(options.PromotionOverlayBaseUrl);
    }

    public async Task<AcceptanceReport> RunAsync(CancellationToken cancellationToken)
    {
        var startedAtUtc = DateTimeOffset.UtcNow;
        var deadline = startedAtUtc + _options.Timeout;
        await WaitForRuntimeAsync(deadline, cancellationToken);
        var token = await AcceptanceHttp.GetTokenAsync(
            _identityClient,
            "ingestion.submit promotion.overlay.publish",
            cancellationToken);

        var fixture = Encoding.UTF8.GetBytes(
            "collector-fixture|berlin-recording-services|studio-example|2026-08-04");
        var evidenceDigest = Convert.ToHexStringLower(SHA256.HashData(fixture));
        var collectorCommandId = Guid.Parse("0198fe00-0000-7000-8000-000000000001");
        var collectorRequest = new SubmitCollectorCandidateRequest(
            collectorCommandId,
            "acceptance-fixture",
            "https://collector.example/fixtures/studio-example",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            CollectorCandidateKindContract.Place,
            "studio-example",
            "Beispiel Tonstudio",
            "https://example.test/studio",
            80m,
            evidenceDigest);
        var candidate = await AcceptanceHttp.PostAsync<
            SubmitCollectorCandidateRequest,
            CollectorCandidateResponse>(
            _collectorClient,
            "api/collector-candidates",
            collectorRequest,
            token,
            headers: null,
            cancellationToken);
        Require(!candidate.Replayed, "First collector command must not be a replay.");
        Require(candidate.SubjectId != Guid.Empty, "Collector must allocate a subject ID.");
        Require(candidate.SubjectRevisionId != Guid.Empty, "Collector must allocate a subject revision ID.");

        var collectorReplay = await AcceptanceHttp.PostAsync<
            SubmitCollectorCandidateRequest,
            CollectorCandidateResponse>(
            _collectorClient,
            "api/collector-candidates",
            collectorRequest,
            token,
            headers: null,
            cancellationToken);
        Require(collectorReplay.Replayed, "Exact collector command replay must be identified.");
        Require(
            collectorReplay.CandidateId == candidate.CandidateId &&
            collectorReplay.SubjectId == candidate.SubjectId &&
            collectorReplay.SubjectRevisionId == candidate.SubjectRevisionId,
            "Exact collector replay must return the original identities.");
        var collectorCollisionStatus = await AcceptanceHttp.PostForStatusAsync(
            _collectorClient,
            "api/collector-candidates",
            collectorRequest with { Title = "Changed under reused command ID" },
            token,
            headers: null,
            cancellationToken);
        Require(
            collectorCollisionStatus == HttpStatusCode.Conflict,
            "Collector command ID reused for changed content must return 409.");

        var acceptanceHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Acceptance-Key"] = _options.AcceptanceKey,
        };
        var catalogSeed = await AcceptanceHttp.PostAsync<CatalogSeedRequest, CatalogSeedResponse>(
            _catalogControlClient,
            "acceptance/catalog/seed",
            new CatalogSeedRequest(
                candidate.SubjectId,
                candidate.SubjectRevisionId,
                candidate.Title,
                candidate.SourceReference,
                candidate.EvidenceDigest,
                candidate.Website,
                candidate.HourlyPrice ?? 0m),
            bearerToken: null,
            acceptanceHeaders,
            cancellationToken);
        Require(catalogSeed.PublicationId != Guid.Empty, "Catalog seed must create a publication.");

        var firstQuery = await AcceptanceHttp.WaitAsync(
            token => TryReadOrganicAsync(
                catalogSeed.PublicationId,
                candidate.Title,
                token),
            deadline,
            "the first Catalog publication in Query",
            cancellationToken);
        Require(
            firstQuery.ListingId == catalogSeed.ListingId,
            "Query must expose the exact Catalog listing identity.");

        var analyticsEvents = new[]
        {
            new RecordAnalyticsInteractionRequest(
                Guid.Parse("0198fe00-0000-7000-8000-000000000101"),
                "berlin-recording-services",
                firstQuery.PublicReadRevisionId,
                firstQuery.ListingId,
                "acceptance-session-1",
                AnalyticsInteractionKindContract.ListingView,
                DateTimeOffset.UtcNow.AddSeconds(-2)),
            new RecordAnalyticsInteractionRequest(
                Guid.Parse("0198fe00-0000-7000-8000-000000000102"),
                "berlin-recording-services",
                firstQuery.PublicReadRevisionId,
                firstQuery.ListingId,
                "acceptance-session-1",
                AnalyticsInteractionKindContract.ContactClick,
                DateTimeOffset.UtcNow.AddSeconds(-1)),
            new RecordAnalyticsInteractionRequest(
                Guid.Parse("0198fe00-0000-7000-8000-000000000103"),
                "berlin-recording-services",
                firstQuery.PublicReadRevisionId,
                firstQuery.ListingId,
                "acceptance-session-1",
                AnalyticsInteractionKindContract.Lead,
                DateTimeOffset.UtcNow.AddSeconds(-1)),
        };
        foreach (var analyticsEvent in analyticsEvents)
        {
            var receipt = await AcceptanceHttp.PostAsync<
                RecordAnalyticsInteractionRequest,
                AnalyticsInteractionReceipt>(
                _analyticsClient,
                "api/analytics/interactions",
                analyticsEvent,
                bearerToken: null,
                headers: null,
                cancellationToken);
            Require(!receipt.Replayed, "First Analytics event submission must not be a replay.");
        }

        var analyticsReplay = await AcceptanceHttp.PostAsync<
            RecordAnalyticsInteractionRequest,
            AnalyticsInteractionReceipt>(
            _analyticsClient,
            "api/analytics/interactions",
            analyticsEvents[0],
            bearerToken: null,
            headers: null,
            cancellationToken);
        Require(analyticsReplay.Replayed, "Exact Analytics event replay must be identified.");
        var analyticsCollisionStatus = await AcceptanceHttp.PostForStatusAsync(
            _analyticsClient,
            "api/analytics/interactions",
            analyticsEvents[0] with { SessionKey = "different-session" },
            bearerToken: null,
            headers: null,
            cancellationToken);
        Require(
            analyticsCollisionStatus == HttpStatusCode.Conflict,
            "Analytics event ID reused for changed content must return 409.");

        var analyticsHeaders = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["X-Analytics-Internal-Key"] = _options.AnalyticsInternalMetricsKey,
        };
        var metrics = await AcceptanceHttp.WaitAsync(
            async token =>
            {
                var value = await AcceptanceHttp.GetAsync<AnalyticsListingMetricsResponse>(
                    _analyticsClient,
                    $"api/analytics/catalogs/berlin-recording-services/listings/{firstQuery.ListingId:D}/metrics",
                    analyticsHeaders,
                    token);
                return value.ListingViews >= 1 &&
                       value.ContactClicks >= 1 &&
                       value.Leads >= 1
                    ? value
                    : null;
            },
            deadline,
            "Analytics listing aggregates",
            cancellationToken);

        var campaignId = Guid.Parse("0198fe00-0000-7000-8000-000000000201");
        var firstPromotionRequest = new PublishPromotionOverlayRequest(
            Guid.Parse("0198fe00-0000-7000-8000-000000000202"),
            "berlin-recording-services",
            firstQuery.PublicReadRevisionId,
            ExpectedCurrentOverlayId: null,
            [
                new PromotionOverlayItemContract(
                    firstQuery.ListingId,
                    campaignId,
                    Position: 1,
                    firstQuery.ResolvedLocale,
                    firstQuery.Title,
                    firstQuery.RoutePath,
                    "Anzeige"),
            ]);
        var firstPromotion = await AcceptanceHttp.PostAsync<
            PublishPromotionOverlayRequest,
            PromotionOverlayPublicationResponse>(
            _promotionClient,
            "api/promotion-overlays",
            firstPromotionRequest,
            token,
            headers: null,
            cancellationToken);
        Require(!firstPromotion.Replayed, "First Promotion overlay command must not be a replay.");
        var firstPromotionReplay = await AcceptanceHttp.PostAsync<
            PublishPromotionOverlayRequest,
            PromotionOverlayPublicationResponse>(
            _promotionClient,
            "api/promotion-overlays",
            firstPromotionRequest,
            token,
            headers: null,
            cancellationToken);
        Require(
            firstPromotionReplay.Replayed &&
            firstPromotionReplay.OverlayId == firstPromotion.OverlayId,
            "Exact Promotion command replay must return the original overlay.");
        var promotionCollisionStatus = await AcceptanceHttp.PostForStatusAsync(
            _promotionClient,
            "api/promotion-overlays",
            firstPromotionRequest with
            {
                Items =
                [
                    firstPromotionRequest.Items[0] with
                    {
                        Title = "Changed under reused command ID",
                    },
                ],
            },
            token,
            headers: null,
            cancellationToken);
        Require(
            promotionCollisionStatus == HttpStatusCode.Conflict,
            "Promotion command ID reused for changed content must return 409.");

        _ = await AcceptanceHttp.WaitAsync(
            token => TryReadSponsoredAsync(
                firstQuery.PublicReadRevisionId,
                firstPromotion.OverlayId,
                firstQuery.ListingId,
                token),
            deadline,
            "the first Promotion overlay in Query",
            cancellationToken);

        var secondEvidenceDigest = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes("collector-fixture|studio-example|revision-2")));
        var secondPublication = await AcceptanceHttp.PostAsync<
            CatalogPublishNextRequest,
            CatalogPublishNextResponse>(
            _catalogControlClient,
            "acceptance/catalog/publish-next",
            new CatalogPublishNextRequest(
                catalogSeed.ListingId,
                catalogSeed.PublicationId,
                candidate.SubjectId,
                candidate.SubjectRevisionId,
                "Beispiel Tonstudio – aktualisiert",
                "https://collector.example/fixtures/studio-example/revision-2",
                secondEvidenceDigest,
                candidate.Website,
                95m),
            bearerToken: null,
            acceptanceHeaders,
            cancellationToken);
        var secondQuery = await AcceptanceHttp.WaitAsync(
            token => TryReadOrganicAsync(
                secondPublication.PublicationId,
                "Beispiel Tonstudio – aktualisiert",
                token),
            deadline,
            "the second Catalog publication in Query",
            cancellationToken);
        Require(
            secondQuery.PublicReadRevisionId != firstQuery.PublicReadRevisionId,
            "A new Catalog publication must create a new public read revision.");
        await RequireSponsoredEmptyAsync(secondQuery.PublicReadRevisionId, cancellationToken);

        var rollback = await AcceptanceHttp.PostAsync<
            CatalogRollbackRequest,
            CatalogRollbackResponse>(
            _catalogControlClient,
            "acceptance/catalog/rollback",
            new CatalogRollbackRequest(
                catalogSeed.PublicationId,
                secondPublication.PublicationId),
            bearerToken: null,
            acceptanceHeaders,
            cancellationToken);
        Require(
            rollback.CurrentPublicationId == catalogSeed.PublicationId && rollback.IsCurrent,
            "Catalog rollback must activate the requested historical publication.");
        var rollbackQuery = await AcceptanceHttp.WaitAsync(
            token => TryReadOrganicAsync(
                catalogSeed.PublicationId,
                candidate.Title,
                token,
                disallowedRevisionIds:
                [firstQuery.PublicReadRevisionId, secondQuery.PublicReadRevisionId]),
            deadline,
            "the rollback Catalog activation in Query",
            cancellationToken);
        await RequireSponsoredEmptyAsync(rollbackQuery.PublicReadRevisionId, cancellationToken);

        var rollbackPromotionRequest = new PublishPromotionOverlayRequest(
            Guid.Parse("0198fe00-0000-7000-8000-000000000203"),
            "berlin-recording-services",
            rollbackQuery.PublicReadRevisionId,
            firstPromotion.OverlayId,
            [
                new PromotionOverlayItemContract(
                    rollbackQuery.ListingId,
                    campaignId,
                    Position: 1,
                    rollbackQuery.ResolvedLocale,
                    rollbackQuery.Title,
                    rollbackQuery.RoutePath,
                    "Anzeige"),
            ]);
        var rollbackPromotion = await AcceptanceHttp.PostAsync<
            PublishPromotionOverlayRequest,
            PromotionOverlayPublicationResponse>(
            _promotionClient,
            "api/promotion-overlays",
            rollbackPromotionRequest,
            token,
            headers: null,
            cancellationToken);
        _ = await AcceptanceHttp.WaitAsync(
            token => TryReadSponsoredAsync(
                rollbackQuery.PublicReadRevisionId,
                rollbackPromotion.OverlayId,
                rollbackQuery.ListingId,
                token),
            deadline,
            "the rollback-bound Promotion overlay in Query",
            cancellationToken);

        var completedAtUtc = DateTimeOffset.UtcNow;
        return new AcceptanceReport(
            startedAtUtc,
            completedAtUtc,
            candidate.CandidateId,
            catalogSeed.ListingId,
            catalogSeed.PublicationId,
            firstQuery.PublicReadRevisionId,
            firstPromotion.OverlayId,
            secondPublication.PublicationId,
            secondQuery.PublicReadRevisionId,
            rollbackQuery.PublicReadRevisionId,
            rollbackPromotion.OverlayId,
            metrics.ListingViews,
            metrics.ContactClicks,
            metrics.Leads,
            new List<string>
            {
                "collector exact replay preserves candidate and subject identities",
                "collector command ID collision fails with HTTP 409",
                "Catalog publication reaches Query through durable asynchronous delivery",
                "Query public read revision is tied to one exact Catalog publication activation",
                "Analytics exact replay is idempotent and changed payload collision fails",
                "Analytics stores keyed session hashes and updates listing aggregates exactly once",
                "Promotion overlay publication is idempotent and delivered through outbox/RabbitMQ",
                "sponsored results are returned only for the exact source public read revision",
                "new Catalog publication invalidates the previous sponsored overlay",
                "Catalog rollback creates a new public read revision for the historical publication",
                "rollback does not resurrect a stale Promotion overlay without explicit republication",
            });
    }

    private async Task WaitForRuntimeAsync(
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        await AcceptanceHttp.WaitForHealthAsync(
            _identityClient,
            "health/live",
            deadline,
            cancellationToken);
        await AcceptanceHttp.WaitForHealthAsync(
            _collectorClient,
            "health/ready",
            deadline,
            cancellationToken);
        await AcceptanceHttp.WaitForHealthAsync(
            _catalogControlClient,
            "health/ready",
            deadline,
            cancellationToken);
        await AcceptanceHttp.WaitForHealthAsync(
            _queryClient,
            "health/live",
            deadline,
            cancellationToken);
        await AcceptanceHttp.WaitForHealthAsync(
            _analyticsClient,
            "health/ready",
            deadline,
            cancellationToken);
        await AcceptanceHttp.WaitForHealthAsync(
            _promotionClient,
            "health/ready",
            deadline,
            cancellationToken);
    }

    private async Task<QueryOrganicSnapshot?> TryReadOrganicAsync(
        Guid expectedSourcePublicationId,
        string expectedTitle,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? disallowedRevisionIds = null)
    {
        using var document = await AcceptanceHttp.GetDocumentAsync(
            _queryClient,
            "api/catalog-query/catalogs/berlin-recording-services/listings?locale=de-DE&pageSize=20",
            cancellationToken);
        var root = document.RootElement;
        var metadata = root.GetProperty("metadata");
        var publicReadRevisionId = metadata.GetProperty("publicReadRevisionId").GetGuid();
        var sourcePublicationId = metadata.GetProperty("sourcePublicationId").GetGuid();
        if (sourcePublicationId != expectedSourcePublicationId ||
            disallowedRevisionIds?.Contains(publicReadRevisionId) == true)
        {
            return null;
        }

        foreach (var listing in root.GetProperty("organic").EnumerateArray())
        {
            var title = listing.GetProperty("title").GetString();
            if (!string.Equals(title, expectedTitle, StringComparison.Ordinal))
            {
                continue;
            }

            var listingId = listing.GetProperty("listingId").GetGuid();
            var resolvedLocale = listing.GetProperty("resolvedLocale").GetString()
                ?? throw new JsonException("Query listing resolvedLocale is absent.");
            var routePath = listing.TryGetProperty("routePath", out var routeProperty)
                ? routeProperty.GetString()
                    ?? throw new JsonException("Query listing routePath is null.")
                : $"/{resolvedLocale}/listings/{listingId:N}";
            return new QueryOrganicSnapshot(
                publicReadRevisionId,
                sourcePublicationId,
                listingId,
                title
                    ?? throw new JsonException("Query listing title is absent."),
                routePath,
                resolvedLocale);
        }

        return null;
    }

    private async Task<SponsoredListingSearchResponse?> TryReadSponsoredAsync(
        Guid publicReadRevisionId,
        Guid expectedOverlayId,
        Guid expectedListingId,
        CancellationToken cancellationToken)
    {
        var response = await AcceptanceHttp.GetAsync<SponsoredListingSearchResponse>(
            _queryClient,
            $"api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE",
            headers: null,
            cancellationToken);
        return response.OverlayId == expectedOverlayId &&
               response.Sponsored.Count == 1 &&
               response.Sponsored[0].ListingId == expectedListingId
            ? response
            : null;
    }

    private async Task RequireSponsoredEmptyAsync(
        Guid publicReadRevisionId,
        CancellationToken cancellationToken)
    {
        var response = await AcceptanceHttp.GetAsync<SponsoredListingSearchResponse>(
            _queryClient,
            $"api/catalog-query/catalogs/berlin-recording-services/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE",
            headers: null,
            cancellationToken);
        Require(
            response.OverlayId is null && response.Sponsored.Count == 0,
            "A public read revision without an exact Promotion overlay must return an explicit empty sponsored slice.");
    }

    private static HttpClient CreateClient(Uri baseAddress) => new()
    {
        BaseAddress = EnsureTrailingSlash(baseAddress),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsoluteUri.EndsWith('/', StringComparison.Ordinal)
            ? value
            : new Uri($"{value.AbsoluteUri}/", UriKind.Absolute);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
