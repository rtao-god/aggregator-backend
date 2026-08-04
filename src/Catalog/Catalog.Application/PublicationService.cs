using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class PublicationService
{
    private const string EventContractIdentity = "aggregator-catalog-events/catalog-publication-activated/1";
    private const string EventRoutingKey = "catalog.publication.activated";
    private readonly IPublicationStore _publicationStore;
    private readonly IListingStore _listingStore;
    private readonly IProductConfigurationStore _configurationStore;
    private readonly IPublicationArtifactStore _artifactStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _generatorBuild;

    public PublicationService(
        IPublicationStore publicationStore,
        IListingStore listingStore,
        IProductConfigurationStore configurationStore,
        IPublicationArtifactStore artifactStore,
        TimeProvider timeProvider,
        string generatorBuild)
    {
        _publicationStore = publicationStore ?? throw new ArgumentNullException(nameof(publicationStore));
        _listingStore = listingStore ?? throw new ArgumentNullException(nameof(listingStore));
        _configurationStore = configurationStore ?? throw new ArgumentNullException(nameof(configurationStore));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _generatorBuild = string.IsNullOrWhiteSpace(generatorBuild)
            ? throw new ArgumentException("A publication generator build identity is required.", nameof(generatorBuild))
            : generatorBuild;
    }

    public async Task<PublicationRequestDto> RequestAsync(
        CreatePublicationRequest request,
        ActorId actorId,
        string idempotencyKey,
        string correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureIdentifier(request.CatalogId, nameof(request.CatalogId));
        EnsureIdentifier(request.ProductConfigurationRevisionId, nameof(request.ProductConfigurationRevisionId));
        EnsureIdentifier(request.TaxonomyRevisionId, nameof(request.TaxonomyRevisionId));
        EnsureIdentifier(request.AttributeRevisionId, nameof(request.AttributeRevisionId));
        EnsureIdentifier(request.MarketAreaRevisionId, nameof(request.MarketAreaRevisionId));
        EnsureIdentifier(actorId.Value, nameof(actorId));
        RequireReason(request.Reason);
        RequireCorrelationId(correlationId);
        ArgumentNullException.ThrowIfNull(request.SelectedListings);
        var normalized = request with
        {
            SelectedListings = request.SelectedListings
                .OrderBy(item => item.ListingId)
                .ThenBy(item => item.ListingRevisionId)
                .ToArray(),
        };
        var catalogId = new CatalogId(normalized.CatalogId);
        var activeConfiguration = await _configurationStore.GetActiveRevisionAsync(catalogId, cancellationToken)
            ?? throw new CatalogCommandException(
                "Catalog.ProductConfiguration",
                "ACTIVE_PRODUCT_CONFIGURATION_REQUIRED",
                409,
                "The catalog has no active product configuration revision.",
                "Explicitly activate a validated configuration revision before requesting publication.");
        if (activeConfiguration.Revision.Id.Value != normalized.ProductConfigurationRevisionId
            || activeConfiguration.TaxonomyRevisionId.Value != normalized.TaxonomyRevisionId
            || activeConfiguration.AttributeRevisionId.Value != normalized.AttributeRevisionId
            || activeConfiguration.MarketAreaRevisionId.Value != normalized.MarketAreaRevisionId)
        {
            throw new CatalogCommandException(
                "Catalog.Publication",
                "PUBLICATION_CONFIGURATION_NOT_ACTIVE",
                409,
                "The publication request does not reference the exact active configuration tuple.",
                "Reload the active Catalog configuration identities and submit an exact request.");
        }

        var actualCurrentPublicationId = await _publicationStore.GetCurrentPublicationIdAsync(catalogId, cancellationToken);
        var expectedCurrentPublicationId = normalized.ExpectedCurrentPublicationId is { } expected
            ? new PublicationId(expected)
            : null;
        if (actualCurrentPublicationId != expectedCurrentPublicationId)
        {
            throw new CatalogCommandException(
                "Catalog.Publication",
                "PUBLICATION_POINTER_CONFLICT",
                409,
                "The current Catalog publication pointer changed.",
                "Reload the exact current publication identity before submitting the request.",
                new Dictionary<string, object?>
                {
                    ["expectedCurrentPublicationId"] = expectedCurrentPublicationId?.Value,
                    ["actualCurrentPublicationId"] = actualCurrentPublicationId?.Value,
                });
        }

        if (normalized.SelectedListings.Count == 0)
        {
            throw new CatalogCommandException(
                "Catalog.Publication",
                "PUBLICATION_LISTING_REQUIRED",
                400,
                "A publication request must select at least one listing revision.",
                "Select every listing revision intended for the exact publication.");
        }

        var selected = new List<SelectedListingRevision>(normalized.SelectedListings.Count);
        var mutatedListings = new List<Listing>(normalized.SelectedListings.Count);
        var expectedStoredRevisions = new Dictionary<ListingId, long>();
        var seen = new HashSet<Guid>();
        var now = _timeProvider.GetUtcNow();
        foreach (var item in normalized.SelectedListings)
        {
            EnsureIdentifier(item.ListingId, nameof(item.ListingId));
            EnsureIdentifier(item.ListingRevisionId, nameof(item.ListingRevisionId));
            if (!seen.Add(item.ListingId))
            {
                throw new CatalogCommandException(
                    "Catalog.Publication",
                    "PUBLICATION_LISTING_DUPLICATE",
                    400,
                    $"Listing '{item.ListingId:D}' is selected more than once.",
                    "Select exactly one revision per listing.");
            }

            var listingId = new ListingId(item.ListingId);
            var listing = await _listingStore.GetAsync(listingId, cancellationToken)
                ?? throw ListingNotFound(item.ListingId);
            if (listing.CatalogId != catalogId)
            {
                throw new CatalogCommandException(
                    "Catalog.Publication",
                    "PUBLICATION_LISTING_CATALOG_MISMATCH",
                    409,
                    $"Listing '{item.ListingId:D}' belongs to a different catalog.",
                    "Select listings owned by the target catalog only.");
            }

            if (listing.State != ListingLifecycleState.Approved
                || listing.CurrentDraftRevisionId?.Value != item.ListingRevisionId)
            {
                throw new CatalogCommandException(
                    "Catalog.Publication",
                    "PUBLICATION_LISTING_NOT_APPROVED",
                    409,
                    $"Listing '{item.ListingId:D}' is not approved at the selected exact revision.",
                    "Complete editorial approval for the exact draft revision before publication.");
            }

            var revision = await _listingStore.GetRevisionAsync(new ListingRevisionId(item.ListingRevisionId), cancellationToken)
                ?? throw new CatalogCommandException(
                    "Catalog.Publication",
                    "LISTING_REVISION_NOT_FOUND",
                    404,
                    $"Listing revision '{item.ListingRevisionId:D}' does not exist.",
                    "Select an existing exact listing revision.");
            ListingConfigurationValidator.ValidateForPublication(listing, revision, activeConfiguration, now);
            expectedStoredRevisions.Add(listing.Id, listing.AggregateRevision);
            listing.RequestPublication(listing.AggregateRevision, actorId, now);
            selected.Add(new SelectedListingRevision(listing.Id, revision.Id));
            mutatedListings.Add(listing);
        }

        var publicationRequest = CatalogPublicationRequest.Create(
            PublicationRequestId.New(),
            PublicationId.New(),
            catalogId,
            expectedCurrentPublicationId,
            activeConfiguration.Revision.Id,
            activeConfiguration.TaxonomyRevisionId,
            activeConfiguration.AttributeRevisionId,
            activeConfiguration.MarketAreaRevisionId,
            selected,
            normalized.Reason,
            actorId,
            now);
        var command = CatalogCommandIdentity.Create(
            $"publication/request/{catalogId.Value:D}",
            idempotencyKey,
            CatalogCanonicalJson.ComputeDigest(normalized));
        var result = await _publicationStore.CreateRequestAsync(
            publicationRequest,
            mutatedListings,
            expectedStoredRevisions,
            command,
            correlationId,
            cancellationToken);
        return CatalogContractMapper.ToDto(result.Value);
    }

    public async Task<bool> ProcessNextAsync(
        string workerIdentity,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workerIdentity) || workerIdentity.Length > 200)
        {
            throw new ArgumentException("A stable worker identity of at most 200 characters is required.", nameof(workerIdentity));
        }

        if (leaseDuration < TimeSpan.FromSeconds(10) || leaseDuration > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration), "Publication lease must be between 10 seconds and 15 minutes.");
        }

        var claimed = await _publicationStore.ClaimNextAsync(workerIdentity, leaseDuration, cancellationToken);
        if (claimed is null)
        {
            return false;
        }

        var processingRevision = claimed.Request.AggregateRevision;
        try
        {
            var sources = await _publicationStore.LoadSourcesAsync(claimed.Request, cancellationToken);
            var configuration = await _configurationStore.GetRevisionAsync(
                claimed.Request.ProductConfigurationRevisionId,
                cancellationToken)
                ?? throw new CatalogCommandException(
                    "Catalog.Publication",
                    "PRODUCT_CONFIGURATION_REVISION_NOT_FOUND",
                    409,
                    "The exact product configuration revision cannot be loaded for publication.",
                    "Restore the immutable configuration revision before retrying the exact work unit.");
            if (configuration.CatalogId != claimed.Request.CatalogId)
            {
                throw new CatalogCommandException(
                    "Catalog.Publication",
                    "PUBLICATION_CONFIGURATION_CATALOG_MISMATCH",
                    409,
                    "The publication configuration belongs to a different catalog.",
                    "Repair the persisted publication request; do not substitute another configuration.");
            }

            var evaluatedAtUtc = _timeProvider.GetUtcNow();
            foreach (var source in sources)
            {
                ListingConfigurationValidator.ValidateForPublication(
                    source.Listing,
                    source.Revision,
                    configuration,
                    evaluatedAtUtc);
            }

            var bundle = CatalogPublicationComposer.Compose(claimed.Request, sources, _generatorBuild);
            var bundleBytes = CatalogCanonicalJson.Serialize(bundle);
            var bundleDigest = CatalogCanonicalJson.ComputeDigest(bundleBytes);
            var artifact = await _artifactStore.PutVerifiedAsync(
                claimed.Request.PublicationId,
                bundleBytes,
                bundleDigest,
                CatalogPublicationComposer.SchemaIdentity,
                cancellationToken);
            var activatedListings = new List<Listing>(sources.Count);
            foreach (var source in sources.OrderBy(item => item.Listing.Id.Value))
            {
                source.Listing.MarkPublished(
                    source.Revision.Id,
                    claimed.Request.PublicationId,
                    source.Listing.AggregateRevision,
                    claimed.Request.RequestedBy,
                    evaluatedAtUtc);
                activatedListings.Add(source.Listing);
            }

            claimed.Request.MarkSealed(processingRevision);
            var publication = new CatalogPublication(
                claimed.Request.PublicationId,
                claimed.Request.CatalogId,
                claimed.Request.ExpectedCurrentPublicationId,
                claimed.Request.ProductConfigurationRevisionId,
                claimed.Request.TaxonomyRevisionId,
                claimed.Request.AttributeRevisionId,
                claimed.Request.MarketAreaRevisionId,
                artifact.ObjectKey,
                artifact.ContentDigest,
                artifact.Size,
                bundle.ListingCount,
                evaluatedAtUtc,
                claimed.Request.RequestedBy);
            var integrationEvent = new CatalogPublicationActivated(
                claimed.Request.PublicationId.Value,
                EventContractIdentity,
                claimed.Request.PublicationId.Value,
                claimed.Request.CatalogId.Value,
                claimed.Request.ExpectedCurrentPublicationId?.Value,
                artifact.ObjectKey,
                artifact.ContentDigest,
                artifact.SchemaIdentity,
                bundle.ListingCount,
                claimed.Request.AggregateRevision,
                evaluatedAtUtc,
                claimed.CorrelationId,
                claimed.CausationId);
            var eventPayloadJson = CatalogCanonicalJson.SerializeToString(integrationEvent);
            var eventPayloadDigest = CatalogCanonicalJson.ComputeDigest(integrationEvent);
            await _publicationStore.CompleteAsync(
                new PublicationCompletion(
                    claimed.Request,
                    publication,
                    artifact,
                    activatedListings,
                    eventPayloadJson,
                    eventPayloadDigest,
                    EventRoutingKey,
                    EventContractIdentity,
                    integrationEvent.MessageId,
                    claimed.CorrelationId,
                    claimed.CausationId),
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var failureCode = NormalizeFailureCode(exception);
            var failureRequest = RestoreProcessingRequest(claimed.Request, processingRevision);
            failureRequest.MarkFailed(failureCode, processingRevision);
            try
            {
                await _publicationStore.FailAsync(
                    failureRequest,
                    failureCode,
                    Truncate(exception.Message, 2_000),
                    cancellationToken);
            }
            catch (Exception persistenceException)
            {
                throw new CatalogCommandException(
                    "Catalog.Publication",
                    "PUBLICATION_FAILURE_PERSISTENCE_FAILED",
                    500,
                    "Publication materialization failed and its owner failure record could not be persisted.",
                    "Restore Catalog database availability, inspect both failures, and retry the exact publication request.",
                    innerException: new AggregateException(exception, persistenceException));
            }

            throw;
        }
    }

    private static CatalogPublicationRequest RestoreProcessingRequest(
        CatalogPublicationRequest request,
        long processingRevision) =>
        CatalogPublicationRequest.Restore(
            request.Id,
            request.PublicationId,
            request.CatalogId,
            request.ExpectedCurrentPublicationId,
            request.ProductConfigurationRevisionId,
            request.TaxonomyRevisionId,
            request.AttributeRevisionId,
            request.MarketAreaRevisionId,
            request.SelectedListings,
            request.Reason,
            request.RequestedBy,
            request.RequestedAtUtc,
            PublicationRequestState.Processing,
            processingRevision,
            null);

    private static string NormalizeFailureCode(Exception exception)
    {
        var source = exception switch
        {
            CatalogCommandException command => command.Code,
            CatalogDomainException domain => domain.Code,
            _ => "PUBLICATION_MATERIALIZATION_FAILED",
        };
        var normalized = new string(source
            .ToLowerInvariant()
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')
            .ToArray())
            .Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "publication-materialization-failed" : normalized;
    }

    private static void RequireCorrelationId(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 128)
        {
            throw new CatalogCommandException(
                "Catalog.Commands",
                "CORRELATION_ID_INVALID",
                400,
                "A correlation identity of at most 128 characters is required.",
                "Send one stable X-Correlation-Id or allow the API middleware to create it.");
        }
    }

    private static void RequireReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1_000)
        {
            throw new CatalogCommandException(
                "Catalog.Publication",
                "PUBLICATION_REASON_INVALID",
                400,
                "Publication reason must contain between 1 and 1000 characters.",
                "Provide a concise auditable publication reason.");
        }
    }

    private static void EnsureIdentifier(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
        {
            throw new CatalogCommandException(
                "Catalog.Contracts",
                "IDENTIFIER_REQUIRED",
                400,
                $"'{parameterName}' must be a non-empty UUID.",
                "Provide the exact owner identity in UUID form.");
        }
    }

    private static CatalogCommandException ListingNotFound(Guid listingId) =>
        new(
            "Catalog.Publication",
            "LISTING_NOT_FOUND",
            404,
            $"Listing '{listingId:D}' does not exist.",
            "Select an existing exact listing identity.");

    private static string Truncate(string value, int maximumLength) =>
        value.Length <= maximumLength ? value : value[..maximumLength];
}
