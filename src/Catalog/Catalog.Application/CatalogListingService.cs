using Aggregator.Catalog.Contracts;
using Aggregator.Catalog.Domain;
using Aggregator.Catalog.Media.Contracts;

namespace Aggregator.Catalog.Application;

public sealed class CatalogListingService(
    ICatalogRepository repository,
    ICatalogMediaPublicationBindingAuthority mediaBindingAuthority,
    ICatalogIdSource idSource,
    TimeProvider timeProvider)
{
    public async Task<ListingResponse> CreateAsync(
        CreateListingRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var catalogKey = CatalogKey.Create(request.CatalogKey);
        var configuration = await repository.GetActiveConfigurationAsync(catalogKey, cancellationToken)
            ?? throw new CatalogConflictException(
                $"Catalog '{catalogKey}' has no active product configuration.");
        var subject = CatalogContractMapper.ToDomain(request.Subject);
        if (!configuration.Catalog.AllowedListingKinds.Contains(subject.Kind))
        {
            throw new CatalogConflictException(
                $"Subject kind '{subject.Kind}' cannot be listed in catalog '{catalogKey}'.");
        }

        var listing = Listing.Create(idSource.CreateId(), catalogKey, subject, timeProvider.GetUtcNow());
        await repository.AddListingAsync(listing, cancellationToken);
        return CatalogContractMapper.ToResponse(listing);
    }

    public async Task<ListingRevisionResponse> AddRevisionAsync(
        Guid listingId,
        CreateListingRevisionRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request.Content);
        ArgumentNullException.ThrowIfNull(request.Content.Media);
        var listing = await RequireListingAsync(listingId, cancellationToken);
        var configuration = await repository.GetConfigurationAsync(
                request.ConfigurationRevisionId,
                cancellationToken)
            ?? throw new CatalogNotFoundException(
                "product-configuration-revision",
                request.ConfigurationRevisionId);

        if (configuration.Catalog.Key != listing.CatalogKey)
        {
            throw new CatalogConflictException(
                $"Configuration '{configuration.RevisionId}' does not belong to listing catalog '{listing.CatalogKey}'.");
        }

        var activeConfiguration = await repository.GetActiveConfigurationAsync(
                listing.CatalogKey,
                cancellationToken)
            ?? throw new CatalogConflictException(
                $"Catalog '{listing.CatalogKey}' has no active product configuration.");
        if (activeConfiguration.RevisionId != configuration.RevisionId)
        {
            throw new CatalogConflictException(
                $"Configuration '{configuration.RevisionId}' is not the active revision for catalog '{listing.CatalogKey}'.");
        }

        var subject = CatalogContractMapper.ToDomain(request.Subject);
        var mediaBindings = await ResolveMediaBindingsAsync(
            listing.CatalogKey.Value,
            request.Content.Media,
            cancellationToken);
        var revisionId = idSource.CreateId();
        var content = CatalogContractMapper.ToDomain(
            subject.Kind,
            request.Content,
            configuration,
            mediaBindings,
            idSource.CreateId);
        var canonicalContent = CatalogCanonicalJson.SerializeListingContent(content);
        var contentDigest = CatalogCanonicalJson.ComputeSha256(canonicalContent);
        var revision = listing.AddDraftRevision(
            revisionId,
            request.ExpectedVersion,
            configuration.RevisionId,
            subject,
            content,
            contentDigest,
            actor.Id,
            timeProvider.GetUtcNow());

        await repository.AddListingRevisionAsync(listing, revision, cancellationToken);
        return CatalogContractMapper.ToResponse(revision);
    }

    public async Task<EditorialDecisionResponse> ApproveAsync(
        Guid listingId,
        ApproveListingRevisionRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var listing = await RequireListingAsync(listingId, cancellationToken);
        var revision = await RequireRevisionAsync(request.RevisionId, listingId, cancellationToken);
        var configuration = await repository.GetConfigurationAsync(
                revision.ConfigurationRevisionId,
                cancellationToken)
            ?? throw new CatalogNotFoundException(
                "product-configuration-revision",
                revision.ConfigurationRevisionId);
        var activeConfiguration = await repository.GetActiveConfigurationAsync(
                listing.CatalogKey,
                cancellationToken)
            ?? throw new CatalogConflictException(
                $"Catalog '{listing.CatalogKey}' has no active product configuration.");
        if (activeConfiguration.RevisionId != configuration.RevisionId)
        {
            throw new CatalogConflictException(
                $"Revision '{revision.Id}' was authored against inactive configuration '{configuration.RevisionId}'.");
        }

        var decision = listing.Approve(
            idSource.CreateId(),
            revision.Id,
            request.ExpectedVersion,
            revision.Content,
            configuration,
            actor.Id,
            timeProvider.GetUtcNow());
        await repository.AddEditorialDecisionAsync(listing, decision, cancellationToken);
        return CatalogContractMapper.ToResponse(decision);
    }

    public async Task<EditorialDecisionResponse> RejectAsync(
        Guid listingId,
        RejectListingRevisionRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var listing = await RequireListingAsync(listingId, cancellationToken);
        _ = await RequireRevisionAsync(request.RevisionId, listingId, cancellationToken);
        var decision = listing.Reject(
            idSource.CreateId(),
            request.RevisionId,
            request.ExpectedVersion,
            actor.Id,
            request.Reason,
            timeProvider.GetUtcNow());
        await repository.AddEditorialDecisionAsync(listing, decision, cancellationToken);
        return CatalogContractMapper.ToResponse(decision);
    }

    public async Task<ListingResponse> ArchiveAsync(
        Guid listingId,
        ArchiveListingRequest request,
        CatalogActor actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        var listing = await RequireListingAsync(listingId, cancellationToken);
        listing.Archive(request.ExpectedVersion, timeProvider.GetUtcNow());
        await repository.ArchiveListingAsync(listing, cancellationToken);
        return CatalogContractMapper.ToResponse(listing);
    }

    public async Task<ListingResponse> GetAsync(Guid listingId, CancellationToken cancellationToken) =>
        CatalogContractMapper.ToResponse(await RequireListingAsync(listingId, cancellationToken));

    private async Task<IReadOnlyDictionary<Guid, CatalogMediaPublicationBindingContract>> ResolveMediaBindingsAsync(
        string catalogKey,
        IReadOnlyList<MediaReferenceContract> references,
        CancellationToken cancellationToken)
    {
        var bindings = new Dictionary<Guid, CatalogMediaPublicationBindingContract>();
        foreach (var reference in references
                     .Select(value => value ?? throw new CatalogContractException(
                         "catalog.media_reference_null",
                         "Listing revision content cannot contain a null media reference."))
                     .OrderBy(value => value.DisplayOrder)
                     .ThenBy(value => value.MediaId)
                     .ThenBy(value => value.VariantId))
        {
            if (bindings.ContainsKey(reference.VariantId))
            {
                throw new CatalogContractException(
                    "catalog.media_variant_duplicate",
                    $"Media variant '{reference.VariantId}' is referenced more than once.");
            }

            var binding = await mediaBindingAuthority.RequirePublishableBindingAsync(
                catalogKey,
                reference.MediaId,
                reference.ExpectedMediaAggregateRevision,
                reference.VariantId,
                cancellationToken);
            bindings.Add(reference.VariantId, binding);
        }

        return bindings;
    }

    private async Task<Listing> RequireListingAsync(Guid listingId, CancellationToken cancellationToken)
    {
        if (listingId == Guid.Empty)
        {
            throw new ArgumentException("Listing ID is required.", nameof(listingId));
        }

        return await repository.GetListingAsync(listingId, cancellationToken)
            ?? throw new CatalogNotFoundException("listing", listingId);
    }

    private async Task<ListingRevision> RequireRevisionAsync(
        Guid revisionId,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        if (revisionId == Guid.Empty)
        {
            throw new ArgumentException("Revision ID is required.", nameof(revisionId));
        }

        var revision = await repository.GetListingRevisionAsync(revisionId, cancellationToken)
            ?? throw new CatalogNotFoundException("listing-revision", revisionId);
        if (revision.ListingId != listingId)
        {
            throw new CatalogConflictException(
                $"Revision '{revisionId}' does not belong to listing '{listingId}'.");
        }

        return revision;
    }
}
