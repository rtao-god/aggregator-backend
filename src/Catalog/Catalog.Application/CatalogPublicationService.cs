using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class CatalogPublicationService(
    ICatalogRepository repository,
    ICatalogPublicationArtifactStore artifactStore,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    /// <summary>Starts a new correlation root for a direct application or operator command.</summary>
    public Task<CatalogPublicationResponse> PublishAsync(
        CreateCatalogPublicationRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken) =>
        PublishAsync(request, actor, CatalogEventContext.StartRoot(), cancellationToken);

    public async Task<CatalogPublicationResponse> PublishAsync(
        CreateCatalogPublicationRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        CatalogPublicationRequestValidator.Validate(request);
        var catalogKey = CatalogKey.Create(request.CatalogKey);
        var publicationId = idSource.CreateId();
        var sequence = await repository.GetNextPublicationSequenceAsync(catalogKey, cancellationToken);
        var preparedPublication = await PrepareAsync(
            request,
            actor,
            eventContext,
            publicationId,
            sequence,
            timeProvider.GetUtcNow(),
            cancellationToken);
        await repository.CommitPublicationAsync(
            preparedPublication.Publication,
            preparedPublication.ExpectedCurrentPublicationId,
            preparedPublication.Listings,
            preparedPublication.OutboxFactory,
            preparedPublication.EligibilityOutboxRequests,
            cancellationToken);
        return CatalogContractMapper.ToResponse(
            preparedPublication.Publication,
            isCurrent: true);
    }

    internal async Task<CatalogPreparedPublication> PrepareAsync(
        CreateCatalogPublicationRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        Guid publicationId,
        long sequence,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        CatalogPublicationRequestValidator.Validate(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);
        if (publicationId == Guid.Empty)
        {
            throw new CatalogContractException(
                "catalog.publication_identity_invalid",
                "Prepared publication ID must be a non-empty UUID.");
        }

        if (sequence <= 0)
        {
            throw new CatalogContractException(
                "catalog.publication_sequence_invalid",
                "Prepared publication sequence must be greater than zero.");
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new CatalogContractException(
                "catalog.publication_timestamp_invalid",
                "Prepared publication timestamp must be normalized to UTC.");
        }

        var catalogKey = CatalogKey.Create(request.CatalogKey);
        var activeConfiguration = await repository.GetActiveConfigurationAsync(catalogKey, cancellationToken)
            ?? throw new CatalogConflictException(
                $"Catalog '{catalogKey}' has no active product configuration.");
        if (activeConfiguration.RevisionId != request.ConfigurationRevisionId)
        {
            throw new CatalogConflictException(
                $"Publication targets configuration '{request.ConfigurationRevisionId}' but catalog '{catalogKey}' is active on '{activeConfiguration.RevisionId}'.");
        }

        var selections = await repository.GetPublicationSelectionsAsync(
            catalogKey,
            request.Selections,
            cancellationToken);
        if (selections.Count != request.Selections.Count)
        {
            throw new CatalogConflictException("The publication selection set is incomplete.");
        }

        foreach (var requestedSelection in request.Selections)
        {
            var selection = selections.Single(candidate => candidate.Listing.Id == requestedSelection.ListingId);
            if (selection.Listing.Version != requestedSelection.ExpectedListingVersion)
            {
                throw new CatalogConcurrencyException(
                    selection.Listing.Id,
                    requestedSelection.ExpectedListingVersion,
                    selection.Listing.Version);
            }

            if (selection.Revision.Id != requestedSelection.ListingRevisionId ||
                selection.Listing.ApprovedRevisionId != selection.Revision.Id)
            {
                throw new CatalogConflictException(
                    $"Listing '{selection.Listing.Id}' does not have exact revision '{requestedSelection.ListingRevisionId}' approved.");
            }

            if (selection.Revision.ConfigurationRevisionId != request.ConfigurationRevisionId)
            {
                throw new CatalogConflictException(
                    $"Listing revision '{selection.Revision.Id}' was authored against configuration '{selection.Revision.ConfigurationRevisionId}'.");
            }

            selection.Revision.Content.EnsurePublishable(activeConfiguration);
            EnsureCurrentRevisionDigest(selection.Revision);
        }

        var artifactKey = $"catalog/{catalogKey.Value}/publications/{publicationId:N}.json";
        var artifact = CatalogPublicationArtifactFactory.Create(
            publicationId,
            activeConfiguration,
            sequence,
            createdAtUtc,
            selections);
        var artifactBytes = CatalogCanonicalJson.SerializePublication(artifact);
        var artifactDigest = CatalogCanonicalJson.ComputeSha256(artifactBytes);
        await artifactStore.PutVerifiedAsync(
            artifactKey,
            artifactBytes,
            artifactDigest,
            cancellationToken);

        var entries = selections.Select(selection => PublicationEntry.Create(
            selection.Listing.Id,
            selection.Revision.Id,
            selection.Revision.Subject.SubjectRevisionId,
            selection.Revision.ContentDigest));
        var publication = CatalogPublication.Create(
            publicationId,
            catalogKey,
            request.ConfigurationRevisionId,
            sequence,
            artifactKey,
            artifactDigest,
            entries,
            actor.Id,
            createdAtUtc);

        var changedListings = new Dictionary<Guid, Listing>();
        foreach (var selection in selections)
        {
            var publishedListing = ListingPublicationMembership.PublishApproved(
                selection.Listing,
                selection.Revision.Id,
                selection.Listing.Version,
                createdAtUtc);
            changedListings.Add(publishedListing.Id, publishedListing);
        }

        var expectedCurrentPublicationId = ToInternalExpectation(request.ExpectedCurrent);
        var previousPublicationId = await repository.GetCurrentPublicationIdAsync(catalogKey, cancellationToken);
        EnsurePointerExpectation(previousPublicationId, expectedCurrentPublicationId);
        var previousPublication = previousPublicationId is null
            ? null
            : await repository.GetPublicationAsync(previousPublicationId.Value, cancellationToken)
              ?? throw new CatalogNotFoundException("catalog-publication", previousPublicationId.Value);
        var eligibilityTransition = await BuildEligibilityTransitionAsync(
            catalogKey,
            selections,
            previousPublication,
            changedListings,
            createdAtUtc,
            eventContext,
            cancellationToken);
        var eventId = idSource.CreateId();

        CatalogOutboxMessage CreateOutbox(long activationRevision)
        {
            var integrationEvent = new CatalogPublicationActivated(
                eventId,
                publication.Id,
                publication.CatalogKey.Value,
                publication.ConfigurationRevisionId,
                publication.Sequence,
                activationRevision,
                publication.ArtifactKey,
                publication.ArtifactDigest,
                PublicationActivationKindContract.Publication,
                previousPublicationId,
                createdAtUtc);
            return CatalogOutboxMessageFactory.Create(
                integrationEvent.EventId,
                CatalogIntegrationEventTypes.PublicationActivated,
                CatalogIntegrationEventContracts.PublicationActivated,
                integrationEvent,
                createdAtUtc,
                eventContext);
        }

        return new CatalogPreparedPublication(
            publication,
            expectedCurrentPublicationId,
            eligibilityTransition.Listings,
            CreateOutbox,
            eligibilityTransition.OutboxRequests);
    }

    /// <summary>Starts a new correlation root for a direct application or operator rollback command.</summary>
    public Task<CatalogPublicationResponse> RollbackAsync(
        string catalogKeyValue,
        RollbackPublicationRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken) =>
        RollbackAsync(catalogKeyValue, request, actor, CatalogEventContext.StartRoot(), cancellationToken);

    public async Task<CatalogPublicationResponse> RollbackAsync(
        string catalogKeyValue,
        RollbackPublicationRequest request,
        CatalogActor actor,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKeyValue);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(eventContext);
        var catalogKey = CatalogKey.Create(catalogKeyValue);
        if (request.TargetPublicationId == request.ExpectedCurrentPublicationId)
        {
            throw new CatalogContractException(
                "catalog.rollback_target_is_current",
                "Rollback target must differ from the expected current publication.");
        }

        var currentPublicationId = await repository.GetCurrentPublicationIdAsync(catalogKey, cancellationToken)
            ?? throw new CatalogConflictException(
                $"Catalog '{catalogKey}' has no current publication to roll back.");
        if (currentPublicationId != request.ExpectedCurrentPublicationId)
        {
            throw new CatalogConflictException(
                $"Catalog '{catalogKey}' expected current publication '{request.ExpectedCurrentPublicationId}' but is at '{currentPublicationId}'.");
        }

        var currentPublication = await repository.GetPublicationAsync(currentPublicationId, cancellationToken)
            ?? throw new CatalogNotFoundException("catalog-publication", currentPublicationId);
        var target = await repository.GetPublicationAsync(request.TargetPublicationId, cancellationToken)
            ?? throw new CatalogNotFoundException("catalog-publication", request.TargetPublicationId);
        if (target.CatalogKey != catalogKey)
        {
            throw new CatalogConflictException(
                $"Publication '{target.Id}' belongs to catalog '{target.CatalogKey}', not '{catalogKey}'.");
        }

        await artifactStore.VerifyAsync(
            target.ArtifactKey,
            target.ArtifactDigest,
            cancellationToken);

        var loadedTargetSelections = await LoadPublicationSelectionsAsync(target, cancellationToken);
        var activatedAtUtc = timeProvider.GetUtcNow();
        var changedListings = new Dictionary<Guid, Listing>();
        var targetSelections = new List<PublicationSelectionState>(loadedTargetSelections.Count);
        foreach (var selection in loadedTargetSelections)
        {
            var restoredListing = ListingPublicationMembership.RestoreExactPublishedRevision(
                selection.Listing,
                selection.Revision.Id,
                activatedAtUtc);
            targetSelections.Add(new PublicationSelectionState(restoredListing, selection.Revision));
            if (!ReferenceEquals(restoredListing, selection.Listing))
            {
                changedListings.Add(restoredListing.Id, restoredListing);
            }
        }

        var eligibilityTransition = await BuildEligibilityTransitionAsync(
            catalogKey,
            targetSelections,
            currentPublication,
            changedListings,
            activatedAtUtc,
            eventContext,
            cancellationToken);
        var publicationPointer = CurrentPublicationPointer.Create(
            catalogKey,
            target.Id,
            target.Sequence,
            activatedAtUtc,
            actor.Id);
        var eventId = idSource.CreateId();

        CatalogOutboxMessage CreateOutbox(long activationRevision)
        {
            var integrationEvent = new CatalogPublicationActivated(
                eventId,
                target.Id,
                target.CatalogKey.Value,
                target.ConfigurationRevisionId,
                target.Sequence,
                activationRevision,
                target.ArtifactKey,
                target.ArtifactDigest,
                PublicationActivationKindContract.Rollback,
                currentPublicationId,
                activatedAtUtc);
            return CatalogOutboxMessageFactory.Create(
                integrationEvent.EventId,
                CatalogIntegrationEventTypes.PublicationActivated,
                CatalogIntegrationEventContracts.PublicationActivated,
                integrationEvent,
                activatedAtUtc,
                eventContext);
        }

        await repository.ActivateExistingPublicationAsync(
            target,
            request.ExpectedCurrentPublicationId,
            publicationPointer,
            CreateOutbox,
            eligibilityTransition.OutboxRequests,
            cancellationToken);
        return CatalogContractMapper.ToResponse(target, isCurrent: true);
    }

    private async Task<IReadOnlyList<PublicationSelectionState>> LoadPublicationSelectionsAsync(
        CatalogPublication publication,
        CancellationToken cancellationToken)
    {
        var selections = new List<PublicationSelectionState>(publication.Entries.Count);
        foreach (var entry in publication.Entries.OrderBy(value => value.ListingId))
        {
            var listing = await repository.GetListingAsync(entry.ListingId, cancellationToken)
                ?? throw new CatalogNotFoundException("listing", entry.ListingId);
            var revision = await repository.GetListingRevisionAsync(
                    entry.ListingRevisionId,
                    cancellationToken)
                ?? throw new CatalogNotFoundException(
                    "listing-revision",
                    entry.ListingRevisionId);
            if (listing.CatalogKey != publication.CatalogKey ||
                revision.ListingId != listing.Id ||
                revision.Id != entry.ListingRevisionId)
            {
                throw new CatalogConflictException(
                    $"Publication '{publication.Id}' contains an invalid listing revision binding for listing '{entry.ListingId}'.");
            }

            selections.Add(new PublicationSelectionState(listing, revision));
        }

        return selections;
    }

    private async Task<CatalogEligibilityTransition> BuildEligibilityTransitionAsync(
        CatalogKey catalogKey,
        IReadOnlyList<PublicationSelectionState> targetSelections,
        CatalogPublication? previousPublication,
        IDictionary<Guid, Listing> changedListings,
        DateTimeOffset occurredAtUtc,
        CatalogEventContext eventContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changedListings);
        var requests = new Dictionary<Guid, CatalogListingPromotionEligibilityOutboxRequest>();
        foreach (var selection in targetSelections.OrderBy(value => value.Listing.Id))
        {
            if (selection.Listing.CatalogKey != catalogKey)
            {
                throw new CatalogConflictException(
                    $"Listing '{selection.Listing.Id}' does not belong to eligibility catalog '{catalogKey}'.");
            }

            if (selection.Listing.PublishedRevisionId != selection.Revision.Id)
            {
                throw new CatalogConflictException(
                    $"Listing '{selection.Listing.Id}' does not point at target publication revision '{selection.Revision.Id}'.");
            }

            requests.Add(
                selection.Listing.Id,
                CatalogListingPromotionEligibilityEventFactory.CreatePublished(
                    selection.Listing,
                    selection.Revision,
                    hasBlockingDispute: false,
                    idSource.CreateId(),
                    occurredAtUtc,
                    eventContext));
        }

        if (previousPublication is not null)
        {
            foreach (var previousEntry in previousPublication.Entries
                         .Where(entry => !requests.ContainsKey(entry.ListingId))
                         .OrderBy(entry => entry.ListingId))
            {
                var listing = await repository.GetListingAsync(
                        previousEntry.ListingId,
                        cancellationToken)
                    ?? throw new CatalogNotFoundException(
                        "listing",
                        previousEntry.ListingId);
                if (listing.CatalogKey != catalogKey)
                {
                    throw new CatalogConflictException(
                        $"Listing '{listing.Id}' does not belong to eligibility catalog '{catalogKey}'.");
                }

                if (listing.PublishedRevisionId != previousEntry.ListingRevisionId)
                {
                    throw new CatalogConflictException(
                        $"Listing '{listing.Id}' public revision '{listing.PublishedRevisionId}' does not match active publication revision '{previousEntry.ListingRevisionId}'.");
                }

                var unpublishedListing = ListingPublicationMembership.RemoveFromPublication(
                    listing,
                    occurredAtUtc);
                changedListings[unpublishedListing.Id] = unpublishedListing;
                requests.Add(
                    unpublishedListing.Id,
                    CatalogListingPromotionEligibilityEventFactory.CreateUnavailable(
                        unpublishedListing,
                        hasBlockingDispute: false,
                        idSource.CreateId(),
                        occurredAtUtc,
                        eventContext));
            }
        }

        return new CatalogEligibilityTransition(
            changedListings.Values
                .OrderBy(value => value.Id)
                .ToArray(),
            requests.Values
                .OrderBy(value => value.ListingId)
                .ToArray());
    }

    private static void EnsureCurrentRevisionDigest(ListingRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        var canonicalContent = CatalogCanonicalJson.SerializeListingContent(revision.Content);
        var currentDigest = CatalogCanonicalJson.ComputeSha256(canonicalContent);
        if (!string.Equals(currentDigest, revision.ContentDigest, StringComparison.Ordinal))
        {
            throw new CatalogContractException(
                "catalog.listing_revision_digest_contract_stale",
                $"Listing revision '{revision.Id}' was not authored with the current immutable content identity contract.");
        }
    }

    private static Guid? ToInternalExpectation(PublicationPointerExpectationContract expectation)
    {
        ArgumentNullException.ThrowIfNull(expectation);
        return expectation.Kind switch
        {
            PointerExpectationKindContract.Absent when expectation.PublicationId is null => null,
            PointerExpectationKindContract.Exact when expectation.PublicationId is { } publicationId && publicationId != Guid.Empty => publicationId,
            _ => throw new CatalogContractException(
                "catalog.publication_pointer_expectation_invalid",
                "Publication pointer expectation must be either explicit absence or an exact non-empty publication ID."),
        };
    }

    private static void EnsurePointerExpectation(Guid? actual, Guid? expected)
    {
        if (actual != expected)
        {
            throw new CatalogConflictException(
                $"Current publication pointer mismatch. Expected '{expected?.ToString() ?? "absent"}', actual '{actual?.ToString() ?? "absent"}'.");
        }
    }

    private sealed record CatalogEligibilityTransition(
        IReadOnlyList<Listing> Listings,
        IReadOnlyList<CatalogListingPromotionEligibilityOutboxRequest> OutboxRequests);
}
