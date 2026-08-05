using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aggregator.Acceptance.Contracts;
using Aggregator.Analytics.Contracts;
using Aggregator.Ingestion.Collector.Contracts;
using Aggregator.Promotion.Contracts;
using Aggregator.Query.Contracts;

namespace Aggregator.Acceptance.Runner;

public sealed class AcceptanceScenario
{
    private const string CatalogKey = "berlin-recording-services";

    private static readonly Guid AnalyticsActorId =
        Guid.Parse("0198ff00-0000-7000-8000-000000000001");

    private readonly AcceptanceOptions _options;
    private readonly HttpClient _identityClient;
    private readonly HttpClient _collectorClient;
    private readonly HttpClient _catalogControlClient;
    private readonly HttpClient _analyticsControlClient;
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
        _analyticsControlClient = CreateClient(options.AnalyticsControlBaseUrl);
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
            "ingestion.submit promotion.overlay.publish analytics.view-listing",
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
        var hourlyPrice = candidate.HourlyPrice
            ?? throw new InvalidOperationException(
                "The accepted collector fixture must contain an observed hourly price.");
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
                hourlyPrice),
            bearerToken: null,
            acceptanceHeaders,
            cancellationToken);
        Require(catalogSeed.PublicationId != Guid.Empty, "Catalog seed must create a publication.");

        var firstQuery = await AcceptanceHttp.WaitAsync(
            tokenValue => TryReadOrganicAsync(
                catalogSeed.PublicationId,
                candidate.Title,
                tokenValue),
            deadline,
            "the first Catalog publication in Query",
            cancellationToken);
        Require(
            firstQuery.ListingId == catalogSeed.ListingId,
            "Query must expose the exact Catalog listing identity.");

        var aggregateDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var activationAtUtc = new DateTimeOffset(
            aggregateDate.ToDateTime(new TimeOnly(11, 55)),
            TimeSpan.Zero);
        _ = await AcceptanceHttp.PostAsync<AnalyticsBootstrapRequest, AnalyticsBootstrapResponse>(
            _analyticsControlClient,
            "acceptance/analytics/bootstrap",
            new AnalyticsBootstrapRequest(
                firstQuery.PublicReadRevisionId,
                CatalogKey,
                firstQuery.BaseProjectionId,
                firstQuery.PromotionOverlayId,
                firstQuery.SafetyOverlayId,
                firstQuery.SourcePublicationId,
                firstQuery.ListingId,
                AnalyticsActorId,
                activationAtUtc,
                AccessSourceRevision: 1),
            bearerToken: null,
            acceptanceHeaders,
            cancellationToken);

        var occurrenceBase = activationAtUtc.AddMinutes(5);
        var analyticsRequests = new[]
        {
            await CreateAnalyticsRequestAsync(
                Guid.Parse("0198fe00-0000-7000-8000-000000000101"),
                InteractionEventKindContract.ListingImpression,
                firstQuery,
                occurrenceBase,
                "search-results",
                PlacementExposureKindContract.Organic,
                cancellationToken),
            await CreateAnalyticsRequestAsync(
                Guid.Parse("0198fe00-0000-7000-8000-000000000102"),
                InteractionEventKindContract.ListingOpened,
                firstQuery,
                occurrenceBase.AddMinutes(1),
                "listing-card",
                PlacementExposureKindContract.Organic,
                cancellationToken),
            await CreateAnalyticsRequestAsync(
                Guid.Parse("0198fe00-0000-7000-8000-000000000103"),
                InteractionEventKindContract.WebsiteClicked,
                firstQuery,
                occurrenceBase.AddMinutes(2),
                "listing-card",
                PlacementExposureKindContract.Organic,
                cancellationToken),
        };
        foreach (var analyticsRequest in analyticsRequests)
        {
            var receipt = await AcceptanceHttp.PostAsync<
                SubmitInteractionEventRequest,
                InteractionEventResponse>(
                _analyticsClient,
                "api/analytics/interaction-events",
                analyticsRequest,
                bearerToken: null,
                headers: null,
                cancellationToken);
            Require(
                receipt.AcceptanceState == InteractionAcceptanceStateContract.Accepted,
                "First Analytics event submission must be accepted.");
            Require(
                receipt.QualityState == TrafficQualityStateContract.Accepted,
                "Acceptance Analytics fixture must remain in accepted traffic quality.");
        }

        var analyticsReplay = await AcceptanceHttp.PostAsync<
            SubmitInteractionEventRequest,
            InteractionEventResponse>(
            _analyticsClient,
            "api/analytics/interaction-events",
            analyticsRequests[0],
            bearerToken: null,
            headers: null,
            cancellationToken);
        Require(
            analyticsReplay.AcceptanceState == InteractionAcceptanceStateContract.AlreadyApplied,
            "Exact Analytics event replay must return the prior semantic result.");
        var analyticsCollisionStatus = await AcceptanceHttp.PostForStatusAsync(
            _analyticsClient,
            "api/analytics/interaction-events",
            analyticsRequests[0] with { PageContext = "listing-card" },
            bearerToken: null,
            headers: null,
            cancellationToken);
        Require(
            analyticsCollisionStatus == HttpStatusCode.Conflict,
            "Analytics event identity reused for changed content must return 409.");

        var aggregateToExclusive = aggregateDate.AddDays(1);
        var aggregateRebuild = await AcceptanceHttp.PostAsync<
            AnalyticsRebuildRequest,
            AnalyticsRebuildResponse>(
            _analyticsControlClient,
            "acceptance/analytics/rebuild",
            new AnalyticsRebuildRequest(aggregateDate, aggregateToExclusive),
            bearerToken: null,
            acceptanceHeaders,
            cancellationToken);
        Require(
            aggregateRebuild.MaterializedMetricCount >= 1,
            "Analytics rebuild must materialize the listing metric row.");

        var metrics = await AcceptanceHttp.GetAsync<DailyListingMetricsResponse[]>(
            _analyticsClient,
            string.Create(
                CultureInfo.InvariantCulture,
                $"api/analytics/listings/{firstQuery.ListingId:D}/daily-metrics?catalogKey={CatalogKey}&fromInclusive={aggregateDate:yyyy-MM-dd}&toExclusive={aggregateToExclusive:yyyy-MM-dd}"),
            token,
            headers: null,
            cancellationToken);
        var metric = metrics.SingleOrDefault(value => value.Date == aggregateDate)
            ?? throw new InvalidOperationException(
                "Analytics did not return the exact rebuilt daily metric row.");
        Require(
            metric.Readiness == AggregateReadinessStateContract.Complete,
            "Analytics daily metric must be complete before numeric counts are consumed.");
        var counts = metric.Counts
            ?? throw new InvalidOperationException(
                "A complete Analytics metric must contain observed counts.");
        Require(counts.OrganicImpressions == 1, "Analytics must count one organic impression.");
        Require(counts.ListingOpens == 1, "Analytics must count one listing open.");
        Require(counts.WebsiteClicks == 1, "Analytics must count one website click.");

        var campaignId = Guid.Parse("0198fe00-0000-7000-8000-000000000201");
        var firstPromotionRequest = new PublishPromotionOverlayRequest(
            Guid.Parse("0198fe00-0000-7000-8000-000000000202"),
            CatalogKey,
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
            tokenValue => TryReadSponsoredAsync(
                firstQuery.PublicReadRevisionId,
                firstPromotion.OverlayId,
                firstQuery.ListingId,
                tokenValue),
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
            tokenValue => TryReadOrganicAsync(
                secondPublication.PublicationId,
                "Beispiel Tonstudio – aktualisiert",
                tokenValue),
            deadline,
            "the second Catalog publication in Query",
            cancellationToken);
        Require(
            secondQuery.PublicReadRevisionId != firstQuery.PublicReadRevisionId,
            "A new Catalog publication must create a new public read revision.");
        await RequireSponsoredUnavailableAsync(secondQuery.PublicReadRevisionId, cancellationToken);

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
            tokenValue => TryReadOrganicAsync(
                catalogSeed.PublicationId,
                candidate.Title,
                tokenValue,
                new HashSet<Guid>
                {
                    firstQuery.PublicReadRevisionId,
                    secondQuery.PublicReadRevisionId,
                }),
            deadline,
            "the rollback Catalog activation in Query",
            cancellationToken);
        await RequireSponsoredUnavailableAsync(rollbackQuery.PublicReadRevisionId, cancellationToken);

        var rollbackPromotionRequest = new PublishPromotionOverlayRequest(
            Guid.Parse("0198fe00-0000-7000-8000-000000000203"),
            CatalogKey,
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
            tokenValue => TryReadSponsoredAsync(
                rollbackQuery.PublicReadRevisionId,
                rollbackPromotion.OverlayId,
                rollbackQuery.ListingId,
                tokenValue),
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
            counts.OrganicImpressions,
            counts.ListingOpens,
            counts.WebsiteClicks,
            aggregateRebuild.MaterializedMetricCount,
            new List<string>
            {
                "collector exact replay preserves candidate and subject identities",
                "collector command ID collision fails with HTTP 409",
                "Catalog publication reaches Query through durable asynchronous delivery",
                "Query response is bound to one exact composite public-read revision",
                "Analytics public-reference and listing-access projections are local",
                "Analytics exact replay returns the prior semantic result and changed payload conflicts",
                "Analytics distinguishes impression, listing open, and website click without inventing a lead",
                "complete aggregate counts are consumed only after explicit readiness proof",
                "Promotion overlay publication is idempotent and delivered through outbox/RabbitMQ",
                "new Catalog publication has no implicit compatible sponsored overlay",
                "Catalog rollback creates a new public-read revision for the historical publication",
                "rollback does not resurrect a stale Promotion overlay without explicit republication",
            });
    }

    private async Task<SubmitInteractionEventRequest> CreateAnalyticsRequestAsync(
        Guid clientEventId,
        InteractionEventKindContract eventKind,
        QueryOrganicSnapshot query,
        DateTimeOffset occurredAtUtc,
        string pageContext,
        PlacementExposureKindContract exposureKind,
        CancellationToken cancellationToken)
    {
        var proof = await AcceptanceHttp.PostAsync<
            IssueAnalyticsAntiAbuseTokenRequest,
            AnalyticsAntiAbuseTokenResponse>(
            _analyticsClient,
            "api/analytics/anti-abuse-tokens",
            new IssueAnalyticsAntiAbuseTokenRequest(clientEventId, occurredAtUtc),
            bearerToken: null,
            headers: null,
            cancellationToken);
        return new SubmitInteractionEventRequest(
            clientEventId,
            eventKind,
            CatalogKey,
            query.ListingId,
            query.PublicReadRevisionId,
            occurredAtUtc,
            pageContext,
            new PlacementContextContract(exposureKind, PlacementId: null, ScopeKey: null),
            ReferrerClassContract.Internal,
            new Dictionary<string, string>(StringComparer.Ordinal),
            ConsentModeContract.AnalyticsAllowed,
            proof.Token);
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
            _analyticsControlClient,
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
        HashSet<Guid>? disallowedRevisionIds = null)
    {
        var response = await AcceptanceHttp.GetAsync<PublicListingSearchResponse>(
            _queryClient,
            $"api/catalog-query/catalogs/{CatalogKey}/listings?locale=de-DE&pageSize=20",
            headers: null,
            cancellationToken);
        if (response.Metadata.SourcePublicationId != expectedSourcePublicationId ||
            disallowedRevisionIds?.Contains(response.Metadata.PublicReadRevisionId) == true)
        {
            return null;
        }

        var listing = response.Organic.FirstOrDefault(value =>
            string.Equals(value.Title, expectedTitle, StringComparison.Ordinal));
        return listing is null
            ? null
            : new QueryOrganicSnapshot(
                response.Metadata.PublicReadRevisionId,
                response.Metadata.BaseProjectionId,
                response.Metadata.PromotionOverlayId,
                response.Metadata.SafetyOverlayId,
                response.Metadata.SourcePublicationId,
                listing.ListingId,
                listing.Title,
                listing.RoutePath,
                listing.ResolvedLocale,
                response.Metadata.GeneratedAtUtc);
    }

    private async Task<SponsoredListingSearchResponse?> TryReadSponsoredAsync(
        Guid publicReadRevisionId,
        Guid expectedOverlayId,
        Guid expectedListingId,
        CancellationToken cancellationToken)
    {
        var response = await AcceptanceHttp.GetAsync<SponsoredListingSearchResponse>(
            _queryClient,
            $"api/catalog-query/catalogs/{CatalogKey}/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE",
            headers: null,
            cancellationToken);
        return response.OverlayId == expectedOverlayId &&
               response.Sponsored.Count == 1 &&
               response.Sponsored[0].ListingId == expectedListingId
            ? response
            : null;
    }

    private async Task RequireSponsoredUnavailableAsync(
        Guid publicReadRevisionId,
        CancellationToken cancellationToken)
    {
        var status = await AcceptanceHttp.GetForStatusAsync(
            _queryClient,
            $"api/catalog-query/catalogs/{CatalogKey}/sponsored?publicReadRevisionId={publicReadRevisionId:D}&locale=de-DE",
            bearerToken: null,
            headers: null,
            cancellationToken);
        Require(
            status == HttpStatusCode.ServiceUnavailable,
            "A public-read revision without an exact Promotion overlay must return typed unavailable state.");
    }

    private static HttpClient CreateClient(Uri baseAddress) => new()
    {
        BaseAddress = EnsureTrailingSlash(baseAddress),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static Uri EnsureTrailingSlash(Uri value) =>
        value.AbsoluteUri.EndsWith('/')
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
