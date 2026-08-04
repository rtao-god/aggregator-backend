using System.Text.RegularExpressions;

namespace Aggregator.Catalog.Domain;

/// <summary>
/// Catalog-owned immutable proposal created from one accepted Ingestion item. It reserves exact
/// subject/listing/revision identities but has no publication transition and is never public by itself.
/// </summary>
public sealed partial class CatalogIngestionDraftProposal
{
    private static readonly Regex CatalogKeyPattern = CatalogKeyRegex();

    private CatalogIngestionDraftProposal(
        Guid id,
        Guid subjectId,
        Guid listingId,
        Guid listingRevisionId,
        Guid commandId,
        string catalogKey,
        Guid configurationRevisionId,
        Guid importBatchId,
        string itemKey,
        int entityKind,
        string contentDigest,
        DateTimeOffset createdAtUtc)
    {
        Id = id;
        SubjectId = subjectId;
        ListingId = listingId;
        ListingRevisionId = listingRevisionId;
        CommandId = commandId;
        CatalogKey = catalogKey;
        ConfigurationRevisionId = configurationRevisionId;
        ImportBatchId = importBatchId;
        ItemKey = itemKey;
        EntityKind = entityKind;
        ContentDigest = contentDigest;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; }

    public Guid SubjectId { get; }

    public Guid ListingId { get; }

    public Guid ListingRevisionId { get; }

    public Guid CommandId { get; }

    public string CatalogKey { get; }

    public Guid ConfigurationRevisionId { get; }

    public Guid ImportBatchId { get; }

    public string ItemKey { get; }

    public int EntityKind { get; }

    public string ContentDigest { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    public static CatalogIngestionDraftProposal Create(
        Guid id,
        Guid subjectId,
        Guid listingId,
        Guid listingRevisionId,
        Guid commandId,
        string catalogKey,
        Guid configurationRevisionId,
        Guid importBatchId,
        string itemKey,
        int entityKind,
        string contentDigest,
        DateTimeOffset createdAtUtc)
    {
        RequireId(id, nameof(id));
        RequireId(subjectId, nameof(subjectId));
        RequireId(listingId, nameof(listingId));
        RequireId(listingRevisionId, nameof(listingRevisionId));
        RequireId(commandId, nameof(commandId));
        RequireId(configurationRevisionId, nameof(configurationRevisionId));
        RequireId(importBatchId, nameof(importBatchId));
        if (id == subjectId || id == listingId || id == listingRevisionId ||
            subjectId == listingId || subjectId == listingRevisionId || listingId == listingRevisionId)
        {
            throw new ArgumentException("Catalog draft proposal identities must be pairwise distinct.");
        }

        if (string.IsNullOrWhiteSpace(catalogKey) || !CatalogKeyPattern.IsMatch(catalogKey))
        {
            throw new ArgumentException("A normalized Catalog key is required.", nameof(catalogKey));
        }

        if (string.IsNullOrWhiteSpace(itemKey) || itemKey.Length > 300 || itemKey.Any(char.IsControl))
        {
            throw new ArgumentException(
                "A stable item key of at most 300 characters is required.",
                nameof(itemKey));
        }

        if (entityKind is not 1 and not 2)
        {
            throw new ArgumentOutOfRangeException(nameof(entityKind), "Supported entity kinds are Place and Provider.");
        }

        if (contentDigest is not { Length: 64 } ||
            contentDigest.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException("A lowercase SHA-256 content digest is required.", nameof(contentDigest));
        }

        if (createdAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Catalog draft creation time must be UTC.", nameof(createdAtUtc));
        }

        return new CatalogIngestionDraftProposal(
            id,
            subjectId,
            listingId,
            listingRevisionId,
            commandId,
            catalogKey,
            configurationRevisionId,
            importBatchId,
            itemKey.Trim(),
            entityKind,
            contentDigest,
            createdAtUtc);
    }

    private static void RequireId(Guid value, string name)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A non-empty Catalog identity is required.", name);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,95}$", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogKeyRegex();
}
