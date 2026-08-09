using System.Data;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository
{
    public async Task CommitPublicationAsync(
        CatalogPublication publication,
        Guid? expectedCurrentPublicationId,
        IReadOnlyList<Listing> listings,
        CatalogPublicationActivationOutboxFactory outboxFactory,
        IReadOnlyList<CatalogListingPromotionEligibilityOutboxRequest> eligibilityOutboxRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publication);
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(outboxFactory);
        ValidateListingMutations(listings, publication.CatalogKey);
        ValidateEligibilityRequests(eligibilityOutboxRequests, publication.CatalogKey);
        try
        {
            await ExecuteInTransactionAsync(async innerCancellationToken =>
            {
                await CommitNewPublicationAsync(
                    publication,
                    expectedCurrentPublicationId,
                    listings,
                    outboxFactory,
                    operation: null,
                    operationCompletedAtUtc: null,
                    innerCancellationToken);
                await AddListingPromotionEligibilityOutboxAsync(
                    eligibilityOutboxRequests,
                    innerCancellationToken);
                await _dbContext.SaveChangesAsync(innerCancellationToken);
            }, cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            if (TryTranslatePublicationActivationFailure(
                    exception,
                    publication,
                    out var activationFailure))
            {
                throw activationFailure;
            }

            throw;
        }
    }

    internal async Task CommitPreparedPublicationWithEligibilityAsync(
        CatalogPreparedPublication preparedPublication,
        CatalogPublicationOperationCompletion completion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedPublication);
        ArgumentNullException.ThrowIfNull(completion);
        if (completion.OperationId == Guid.Empty || completion.LeaseToken == Guid.Empty)
        {
            throw new ArgumentException(
                "Publication operation and lease token IDs are required.",
                nameof(completion));
        }

        if (completion.CompletedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Publication operation completion timestamp must be normalized to UTC.",
                nameof(completion));
        }

        var publication = preparedPublication.Publication;
        ValidateListingMutations(preparedPublication.Listings, publication.CatalogKey);
        ValidateEligibilityRequests(
            preparedPublication.EligibilityOutboxRequests,
            publication.CatalogKey);
        try
        {
            await ExecuteInTransactionAsync(async innerCancellationToken =>
            {
                var operation = await _dbContext.PublicationOperations
                    .SingleOrDefaultAsync(
                        row => row.Id == completion.OperationId,
                        innerCancellationToken)
                    ?? throw new CatalogPublicationOperationLeaseLostException(
                        completion.OperationId);
                ValidatePublicationOperationLease(operation, publication, completion);
                await CommitNewPublicationAsync(
                    publication,
                    preparedPublication.ExpectedCurrentPublicationId,
                    preparedPublication.Listings,
                    preparedPublication.OutboxFactory,
                    operation,
                    completion.CompletedAtUtc,
                    innerCancellationToken);
                await AddListingPromotionEligibilityOutboxAsync(
                    preparedPublication.EligibilityOutboxRequests,
                    innerCancellationToken);
                await _dbContext.SaveChangesAsync(innerCancellationToken);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new CatalogPublicationOperationLeaseLostException(completion.OperationId);
        }
        catch (DbUpdateException exception)
        {
            if (TryTranslatePublicationActivationFailure(
                    exception,
                    publication,
                    out var activationFailure))
            {
                throw activationFailure;
            }

            throw;
        }
    }

    public async Task ActivateExistingPublicationAsync(
        CatalogPublication targetPublication,
        Guid expectedCurrentPublicationId,
        CurrentPublicationPointer publicationPointer,
        CatalogPublicationActivationOutboxFactory outboxFactory,
        IReadOnlyList<CatalogListingPromotionEligibilityOutboxRequest> eligibilityOutboxRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetPublication);
        ArgumentNullException.ThrowIfNull(publicationPointer);
        ArgumentNullException.ThrowIfNull(outboxFactory);
        ValidateEligibilityRequests(
            eligibilityOutboxRequests,
            targetPublication.CatalogKey);
        var listings = eligibilityOutboxRequests
            .Select(request => request.Listing)
            .ToArray();
        ValidateListingMutations(listings, targetPublication.CatalogKey);
        try
        {
            await ExecuteInTransactionAsync(async innerCancellationToken =>
            {
                var targetExists = await _dbContext.Publications
                    .AsNoTracking()
                    .AnyAsync(
                        row => row.Id == targetPublication.Id &&
                               row.CatalogKey == targetPublication.CatalogKey.Value,
                        innerCancellationToken);
                if (!targetExists)
                {
                    throw new CatalogNotFoundException(
                        "catalog-publication",
                        targetPublication.Id);
                }

                var current = await _dbContext.CurrentPublications
                    .SingleOrDefaultAsync(
                        row => row.CatalogKey == targetPublication.CatalogKey.Value,
                        innerCancellationToken)
                    ?? throw new CatalogConflictException(
                        $"Catalog '{targetPublication.CatalogKey}' has no current publication.");
                EnsurePublicationPointer(
                    current.PublicationId,
                    expectedCurrentPublicationId,
                    targetPublication.CatalogKey);
                var activationRevision = await AllocatePublicationActivationRevisionAsync(
                    targetPublication.CatalogKey,
                    innerCancellationToken);
                var outboxMessage = outboxFactory(activationRevision);

                foreach (var listing in listings)
                {
                    var listingRow = await RequireTrackedListingAsync(
                        listing.Id,
                        innerCancellationToken);
                    ApplyListingMutation(listingRow, listing);
                }

                current.PublicationId = publicationPointer.PublicationId;
                current.PublicationSequence = publicationPointer.PublicationSequence;
                current.ActivatedAtUtc = publicationPointer.ActivatedAtUtc;
                current.ActivatedByActorId = publicationPointer.ActivatedByActorId;
                AddOutbox(outboxMessage);
                await AddListingPromotionEligibilityOutboxAsync(
                    eligibilityOutboxRequests,
                    innerCancellationToken);
                await _dbContext.SaveChangesAsync(innerCancellationToken);
            }, cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            if (TryTranslatePublicationActivationFailure(
                    exception,
                    targetPublication,
                    out var activationFailure))
            {
                throw activationFailure;
            }

            throw;
        }
    }

    public Task ArchiveListingAsync(
        Listing listing,
        CatalogListingPromotionEligibilityOutboxRequest eligibilityOutboxRequest,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(listing);
            ArgumentNullException.ThrowIfNull(eligibilityOutboxRequest);
            ValidateListingMutations(new[] { listing }, listing.CatalogKey);
            ValidateEligibilityRequests(
                new[] { eligibilityOutboxRequest },
                listing.CatalogKey);
            if (eligibilityOutboxRequest.ListingId != listing.Id)
            {
                throw new CatalogContractException(
                    "catalog.promotion_eligibility_archive_identity_mismatch",
                    $"Eligibility event request does not belong to archived listing '{listing.Id}'.");
            }

            var row = await RequireTrackedListingAsync(
                listing.Id,
                innerCancellationToken);
            ApplyListingMutation(row, listing);
            await AddListingPromotionEligibilityOutboxAsync(
                new[] { eligibilityOutboxRequest },
                innerCancellationToken);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    private static void ValidateListingMutations(
        IReadOnlyList<Listing> listings,
        CatalogKey expectedCatalogKey)
    {
        ArgumentNullException.ThrowIfNull(listings);
        ArgumentNullException.ThrowIfNull(expectedCatalogKey);
        if (listings.Select(listing => listing.Id).Distinct().Count() != listings.Count)
        {
            throw new CatalogContractException(
                "catalog.publication_listing_mutation_duplicate",
                "A Catalog publication transaction cannot mutate the same listing twice.");
        }

        foreach (var listing in listings)
        {
            ArgumentNullException.ThrowIfNull(listing);
            if (listing.CatalogKey != expectedCatalogKey)
            {
                throw new CatalogContractException(
                    "catalog.publication_listing_mutation_catalog_mismatch",
                    $"Listing '{listing.Id}' belongs to catalog '{listing.CatalogKey}', not '{expectedCatalogKey}'.");
            }
        }
    }

    private static void ValidateEligibilityRequests(
        IReadOnlyList<CatalogListingPromotionEligibilityOutboxRequest> requests,
        CatalogKey expectedCatalogKey)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(expectedCatalogKey);
        if (requests.Select(request => request.ListingId).Distinct().Count() != requests.Count)
        {
            throw new CatalogContractException(
                "catalog.promotion_eligibility_request_duplicate",
                "A Catalog transaction cannot publish multiple eligibility events for the same listing.");
        }

        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.CatalogKey);
            ArgumentNullException.ThrowIfNull(request.OutboxFactory);
            if (request.ListingId == Guid.Empty)
            {
                throw new CatalogContractException(
                    "catalog.promotion_eligibility_listing_invalid",
                    "Catalog Promotion eligibility event request contains an empty listing ID.");
            }

            if (request.CatalogKey != expectedCatalogKey)
            {
                throw new CatalogContractException(
                    "catalog.promotion_eligibility_catalog_mismatch",
                    $"Listing '{request.ListingId}' eligibility event belongs to catalog '{request.CatalogKey}', not '{expectedCatalogKey}'.");
            }
        }
    }

    private async Task AddListingPromotionEligibilityOutboxAsync(
        IReadOnlyList<CatalogListingPromotionEligibilityOutboxRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests.OrderBy(value => value.ListingId))
        {
            var eligibilityRevision = await AllocateListingPromotionEligibilityRevisionAsync(
                request.CatalogKey,
                request.ListingId,
                cancellationToken);
            AddOutbox(request.OutboxFactory(eligibilityRevision));
        }
    }

    private async Task<long> AllocateListingPromotionEligibilityRevisionAsync(
        CatalogKey catalogKey,
        Guid listingId,
        CancellationToken cancellationToken)
    {
        var currentTransaction = _dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "Listing Promotion eligibility revision must be allocated inside the Catalog business transaction.");
        var connection = _dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Catalog database connection is not open for listing Promotion eligibility revision allocation.");
        }

        await using var command = connection.CreateCommand();
        command.Transaction = currentTransaction.GetDbTransaction();
        command.CommandText = """
            INSERT INTO catalog.listing_promotion_eligibility_sequence (
                catalog_key,
                listing_id,
                next_revision)
            VALUES (@catalog_key, @listing_id, 2)
            ON CONFLICT (catalog_key, listing_id)
            DO UPDATE SET next_revision =
                catalog.listing_promotion_eligibility_sequence.next_revision + 1
            RETURNING next_revision - 1;
            """;
        command.Parameters.Add(new NpgsqlParameter<string>(
            "catalog_key",
            catalogKey.Value));
        command.Parameters.Add(new NpgsqlParameter<Guid>("listing_id", listingId));
        var result = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "Listing Promotion eligibility revision allocator returned no value.");
        return Convert.ToInt64(
            result,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Canonical publication-operation committer that includes every listing eligibility event in the same transaction.
/// </summary>
public sealed class CatalogPublicationOperationCommitter(
    EfCatalogRepository repository) : ICatalogPublicationOperationCommitter
{
    public Task CommitAsync(
        CatalogPreparedPublication preparedPublication,
        CatalogPublicationOperationCompletion completion,
        CancellationToken cancellationToken) =>
        repository.CommitPreparedPublicationWithEligibilityAsync(
            preparedPublication,
            completion,
            cancellationToken);
}
