using System.Data;
using Aggregator.Catalog.Application;
using Aggregator.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Aggregator.Catalog.Infrastructure;

public sealed partial class EfCatalogRepository
{
    public async Task<long> GetNextPublicationSequenceAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        await _dbContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = _dbContext.Database.GetDbConnection().CreateCommand();
            command.CommandText = """
                INSERT INTO catalog.publication_sequence (catalog_key, next_sequence)
                VALUES (@catalog_key, 2)
                ON CONFLICT (catalog_key)
                DO UPDATE SET next_sequence = catalog.publication_sequence.next_sequence + 1
                RETURNING next_sequence - 1;
                """;
            command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey.Value));
            var result = await command.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("Publication sequence allocator returned no value.");
            return Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            await _dbContext.Database.CloseConnectionAsync();
        }
    }

    public async Task<Guid?> GetCurrentPublicationIdAsync(
        CatalogKey catalogKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(catalogKey);
        return await _dbContext.CurrentPublications
            .AsNoTracking()
            .Where(row => row.CatalogKey == catalogKey.Value)
            .Select(row => (Guid?)row.PublicationId)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<CatalogPublication?> GetPublicationAsync(
        Guid publicationId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.Publications
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == publicationId, cancellationToken);
        if (row is null)
        {
            return null;
        }

        var entries = await _dbContext.PublicationEntries
            .AsNoTracking()
            .Where(entry => entry.PublicationId == publicationId)
            .OrderBy(entry => entry.ListingId)
            .Select(entry => PublicationEntry.Create(
                entry.ListingId,
                entry.ListingRevisionId,
                entry.SubjectRevisionId,
                entry.ContentDigest))
            .ToArrayAsync(cancellationToken);
        return CatalogPublication.Create(
            row.Id,
            CatalogKey.Create(row.CatalogKey),
            row.ConfigurationRevisionId,
            row.Sequence,
            row.ArtifactKey,
            row.ArtifactDigest,
            entries,
            row.CreatedByActorId,
            row.CreatedAtUtc);
    }

    public Task CommitPublicationAsync(
        CatalogPublication publication,
        Guid? expectedCurrentPublicationId,
        IReadOnlyList<Listing> listings,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(publication);
            ArgumentNullException.ThrowIfNull(listings);
            ArgumentNullException.ThrowIfNull(outboxMessage);
            var current = await _dbContext.CurrentPublications
                .SingleOrDefaultAsync(
                    row => row.CatalogKey == publication.CatalogKey.Value,
                    innerCancellationToken);
            EnsurePublicationPointer(current?.PublicationId, expectedCurrentPublicationId, publication.CatalogKey);

            _dbContext.Publications.Add(new CatalogPublicationRow
            {
                Id = publication.Id,
                CatalogKey = publication.CatalogKey.Value,
                ConfigurationRevisionId = publication.ConfigurationRevisionId,
                Sequence = publication.Sequence,
                ArtifactKey = publication.ArtifactKey,
                ArtifactDigest = publication.ArtifactDigest,
                CreatedByActorId = publication.CreatedByActorId,
                CreatedAtUtc = publication.CreatedAtUtc,
            });
            foreach (var entry in publication.Entries)
            {
                _dbContext.PublicationEntries.Add(new CatalogPublicationEntryRow
                {
                    PublicationId = publication.Id,
                    ListingId = entry.ListingId,
                    ListingRevisionId = entry.ListingRevisionId,
                    SubjectRevisionId = entry.SubjectRevisionId,
                    ContentDigest = entry.ContentDigest,
                });
            }

            foreach (var listing in listings)
            {
                var listingRow = await RequireTrackedListingAsync(listing.Id, innerCancellationToken);
                ApplyListingMutation(listingRow, listing);
            }

            if (current is null)
            {
                current = new CurrentCatalogPublicationRow
                {
                    CatalogKey = publication.CatalogKey.Value,
                    PublicationId = publication.Id,
                    PublicationSequence = publication.Sequence,
                    ActivatedAtUtc = publication.CreatedAtUtc,
                    ActivatedByActorId = publication.CreatedByActorId,
                };
                _dbContext.CurrentPublications.Add(current);
            }
            else
            {
                current.PublicationId = publication.Id;
                current.PublicationSequence = publication.Sequence;
                current.ActivatedAtUtc = publication.CreatedAtUtc;
                current.ActivatedByActorId = publication.CreatedByActorId;
            }

            AddOutbox(outboxMessage);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    public Task ActivateExistingPublicationAsync(
        CatalogPublication targetPublication,
        Guid expectedCurrentPublicationId,
        CurrentPublicationPointer publicationPointer,
        CatalogOutboxMessage outboxMessage,
        CancellationToken cancellationToken) =>
        ExecuteInTransactionAsync(async innerCancellationToken =>
        {
            ArgumentNullException.ThrowIfNull(targetPublication);
            ArgumentNullException.ThrowIfNull(publicationPointer);
            ArgumentNullException.ThrowIfNull(outboxMessage);
            var targetExists = await _dbContext.Publications
                .AsNoTracking()
                .AnyAsync(
                    row => row.Id == targetPublication.Id && row.CatalogKey == targetPublication.CatalogKey.Value,
                    innerCancellationToken);
            if (!targetExists)
            {
                throw new CatalogNotFoundException("catalog-publication", targetPublication.Id);
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

            current.PublicationId = publicationPointer.PublicationId;
            current.PublicationSequence = publicationPointer.PublicationSequence;
            current.ActivatedAtUtc = publicationPointer.ActivatedAtUtc;
            current.ActivatedByActorId = publicationPointer.ActivatedByActorId;
            AddOutbox(outboxMessage);
            await _dbContext.SaveChangesAsync(innerCancellationToken);
        }, cancellationToken);

    private void AddOutbox(CatalogOutboxMessage message)
    {
        _dbContext.OutboxMessages.Add(new CatalogOutboxRow
        {
            Id = message.Id,
            EventType = message.EventType,
            EventRevision = message.EventRevision,
            Payload = message.Payload,
            OccurredAtUtc = message.OccurredAtUtc,
            PublishedAtUtc = null,
            AttemptCount = 0,
            LastError = null,
        });
    }

    private static void EnsurePublicationPointer(
        Guid? actualPublicationId,
        Guid? expectedPublicationId,
        CatalogKey catalogKey)
    {
        if (actualPublicationId != expectedPublicationId)
        {
            throw new CatalogConflictException(
                $"Catalog '{catalogKey}' expected current publication '{expectedPublicationId?.ToString() ?? "absent"}' but is at '{actualPublicationId?.ToString() ?? "absent"}'.");
        }
    }
}
