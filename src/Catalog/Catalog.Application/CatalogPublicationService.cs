using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;

namespace Aggregator.Catalog.Application;

public sealed class CatalogPublicationService(
    ICatalogRepository repository,
    ICatalogPublicationArtifactStore artifactStore,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<CatalogPublicationResponse> PublishAsync(
        CreateCatalogPublicationV1Request request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request.Selections);
        if (request.Selections.Count == 0)
        {
            throw new CatalogContractException(
                "catalog.publication_empty",
                "A publication must contain at least one exact listing revision selection.");
        }

        var duplicateListings = request.Selections
            .GroupBy(selection => selection.ListingId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicateListings.Length > 0)
        {
            throw new CatalogContractException(
                "catalog.publication_duplicate_listing",
                $"Publication contains duplicate listings: {string.Join(", ", duplicateListings)}.");
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
        }

        var publicationId = idSource.CreateId();
        var sequence = await repository.GetNextPublicationSequenceAsync(catalogKey, cancellationToken);
        var createdAtUtc = timeProvider.GetUtcNow();
        var artifactKey = $"catalog/{catalogKey.Value}/publications/{publicationId:N}.json";
        var artifact = CatalogPublicationArtifactFactory.Create(
            publicationId,
            catalogKey,
            request.ConfigurationRevisionId,
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

        foreach (var selection in selections)
        {
            selection.Listing.MarkPublished(
                selection.Revision.Id,
                selection.Listing.Version,
                createdAtUtc);
        }

        var expectedCurrentPublicationId = ToInternalExpectation(request.ExpectedCurrent);
        var previousPublicationId = await repository.GetCurrentPublicationIdAsync(catalogKey, cancellationToken);
        EnsurePointerExpectation(previousPublicationId, expectedCurrentPublicationId);
        var integrationEvent = new CatalogPublicationActivatedV1(
            idSource.CreateId(),
            publication.Id,
            publication.CatalogKey.Value,
            publication.ConfigurationRevisionId,
            publication.Sequence,
            publication.ArtifactKey,
            publication.ArtifactDigest,
            PublicationActivationKindContract.Publication,
            previousPublicationId,
            createdAtUtc);
        var outboxMessage = new CatalogOutboxMessage(
            integrationEvent.EventId,
            CatalogIntegrationEventTypes.PublicationActivatedV1,
            EventRevision: 1,
            CatalogCanonicalJson.SerializeEvent(integrationEvent),
            createdAtUtc);

        await repository.CommitPublicationAsync(
            publication,
            expectedCurrentPublicationId,
            selections.Select(selection => selection.Listing).ToArray(),
            outboxMessage,
            cancellationToken);
        return CatalogContractMapper.ToResponse(publication, isCurrent: true);
    }

    public async Task<CatalogPublicationResponse> RollbackAsync(
        CatalogKey catalogKey,
        RollbackPublicationRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
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

        var target = await repository.GetPublicationAsync(request.TargetPublicationId, cancellationToken)
            ?? throw new CatalogNotFoundException("catalog-publication", request.TargetPublicationId);
        if (target.CatalogKey != catalogKey)
        {
            throw new CatalogConflictException(
                $"Publication '{target.Id}' belongs to catalog '{target.CatalogKey}', not '{catalogKey}'.");
        }

        var activatedAtUtc = timeProvider.GetUtcNow();
        var publicationPointer = CurrentPublicationPointer.Create(
            catalogKey,
            target.Id,
            target.Sequence,
            activatedAtUtc,
            actor.Id);
        var integrationEvent = new CatalogPublicationActivatedV1(
            idSource.CreateId(),
            target.Id,
            target.CatalogKey.Value,
            target.ConfigurationRevisionId,
            target.Sequence,
            target.ArtifactKey,
            target.ArtifactDigest,
            PublicationActivationKindContract.Rollback,
            currentPublicationId,
            activatedAtUtc);
        var outboxMessage = new CatalogOutboxMessage(
            integrationEvent.EventId,
            CatalogIntegrationEventTypes.PublicationActivatedV1,
            EventRevision: 1,
            CatalogCanonicalJson.SerializeEvent(integrationEvent),
            activatedAtUtc);

        await repository.ActivateExistingPublicationAsync(
            target,
            request.ExpectedCurrentPublicationId,
            publicationPointer,
            outboxMessage,
            cancellationToken);
        return CatalogContractMapper.ToResponse(target, isCurrent: true);
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
}
