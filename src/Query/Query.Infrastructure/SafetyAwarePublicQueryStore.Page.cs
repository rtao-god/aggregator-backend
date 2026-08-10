using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public sealed partial class SafetyAwarePublicQueryStore
{
    public async Task<PublicReadPageSnapshot?> ReadPageAsync(
        string catalogKey,
        Guid? afterListingId,
        int maximumDocuments,
        PublicListingSearchCriteria criteria,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (maximumDocuments is < 1 or > InnerPageSize)
        {
            throw StoreFailure(
                "QUERY_SAFETY_PAGE_LIMIT_INVALID",
                "Safety-aware Query store received an invalid page limit.",
                "Correct the Query application request before reading persistence.");
        }

        PublicReadPageSnapshot? firstSnapshot = null;
        QueryVisibilitySafetyFilter? safety = null;
        var documents = new List<QueryListingDocument>(maximumDocuments);
        var rawCursor = afterListingId;
        while (documents.Count < maximumDocuments)
        {
            var raw = await _inner.ReadPageAsync(
                catalogKey,
                rawCursor,
                InnerPageSize,
                criteria,
                readAtUtc,
                cancellationToken);
            if (raw is null)
            {
                if (firstSnapshot is null)
                {
                    return null;
                }

                break;
            }

            firstSnapshot ??= raw;
            if (raw.Revision.Id != firstSnapshot.Revision.Id)
            {
                throw new QueryReadException(
                    "Query.PublicReadRevision",
                    "QUERY_PUBLIC_READ_CHANGED_DURING_PAGE",
                    503,
                    "Public-read revision changed while Query was filling a safety-filtered page.",
                    "Retry the request against one stable public-read revision.");
            }

            safety ??= await LoadSafetyAsync(raw.Revision, readAtUtc, cancellationToken);
            foreach (var document in raw.Documents)
            {
                if (!safety.IsListingVisible(document))
                {
                    continue;
                }

                documents.Add(safety.FilterChildren(document));
                if (documents.Count == maximumDocuments)
                {
                    break;
                }
            }

            if (raw.Documents.Count < InnerPageSize)
            {
                break;
            }

            var nextRawCursor = raw.Documents[^1].ListingId;
            if (rawCursor == nextRawCursor)
            {
                throw StoreFailure(
                    "QUERY_SAFETY_PAGINATION_STALLED",
                    "Safety-aware Query pagination did not advance its raw listing cursor.",
                    "Inspect ordering and stable listing identities in the active base projection.");
            }

            rawCursor = nextRawCursor;
        }

        var ownerSnapshot = firstSnapshot
            ?? throw StoreFailure(
                "QUERY_SAFETY_PAGE_SNAPSHOT_MISSING",
                "Safety-aware Query page completed without an owner snapshot.",
                "Inspect the public Query store composition.");
        var ownerSafety = safety
            ?? await LoadSafetyAsync(ownerSnapshot.Revision, readAtUtc, cancellationToken);
        var sponsored = ownerSnapshot.SponsoredDocuments
            .Where(item => ownerSafety.IsListingVisible(item.Document))
            .Select(item => new PublicSponsoredListingSnapshot(
                item.Placement,
                ownerSafety.FilterChildren(item.Document)))
            .ToArray();
        var facets = await ReadFacetCountsAsync(
            ownerSnapshot.Revision,
            readAtUtc,
            cancellationToken);
        return new PublicReadPageSnapshot(
            ownerSnapshot.Revision,
            ownerSnapshot.LocalePolicy,
            documents,
            sponsored,
            facets.CategoryCounts,
            facets.DistrictCounts,
            facets.ListingKindCounts,
            facets.ContactKindCounts);
    }

    private async Task<SafetyFacetSnapshot> ReadFacetCountsAsync(
        PublicReadRevision revision,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureCatalogNotBlockedAsync(connection, revision.CatalogKey, cancellationToken);

        var categories = await ReadStringFacetCountsAsync(
            connection,
            """
            SELECT category.category_key,
                   count(DISTINCT category.listing_id)::integer
            FROM documents.listing_category category
            WHERE category.base_projection_id = @base_projection_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item item
                  WHERE item.overlay_id = @safety_overlay_id
                    AND item.target_kind = 'listing'
                    AND item.listing_id = category.listing_id
                    AND item.starts_at_utc <= @read_at_utc
                    AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM documents.listing_localization localization
                  JOIN projection.visibility_safety_overlay_item item
                    ON item.overlay_id = @safety_overlay_id
                   AND item.target_kind = 'route'
                   AND item.target_key = localization.route_path
                   AND item.starts_at_utc <= @read_at_utc
                   AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
                  WHERE localization.base_projection_id = category.base_projection_id
                    AND localization.listing_id = category.listing_id
              )
            GROUP BY category.category_key
            ORDER BY category.category_key;
            """,
            revision,
            readAtUtc,
            "category",
            cancellationToken);

        var districts = await ReadStringFacetCountsAsync(
            connection,
            """
            SELECT geography.district_key,
                   count(*)::integer
            FROM documents.listing_geography geography
            WHERE geography.base_projection_id = @base_projection_id
              AND geography.district_key IS NOT NULL
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item item
                  WHERE item.overlay_id = @safety_overlay_id
                    AND item.target_kind = 'listing'
                    AND item.listing_id = geography.listing_id
                    AND item.starts_at_utc <= @read_at_utc
                    AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM documents.listing_localization localization
                  JOIN projection.visibility_safety_overlay_item item
                    ON item.overlay_id = @safety_overlay_id
                   AND item.target_kind = 'route'
                   AND item.target_key = localization.route_path
                   AND item.starts_at_utc <= @read_at_utc
                   AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
                  WHERE localization.base_projection_id = geography.base_projection_id
                    AND localization.listing_id = geography.listing_id
              )
            GROUP BY geography.district_key
            ORDER BY geography.district_key;
            """,
            revision,
            readAtUtc,
            "district",
            cancellationToken);

        var listingKinds = await ReadListingKindFacetCountsAsync(
            connection,
            revision,
            readAtUtc,
            cancellationToken);
        var contactKinds = await ReadContactKindFacetCountsAsync(
            connection,
            revision,
            readAtUtc,
            cancellationToken);

        return new SafetyFacetSnapshot(
            categories,
            districts,
            listingKinds,
            contactKinds);
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadStringFacetCountsAsync(
        NpgsqlConnection connection,
        string sql,
        PublicReadRevision revision,
        DateTimeOffset readAtUtc,
        string facetKind,
        CancellationToken cancellationToken)
    {
        await using var command = CreateFacetCommand(connection, sql, revision, readAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            var count = reader.GetInt32(1);
            if (string.IsNullOrWhiteSpace(key) || count < 1 || !result.TryAdd(key, count))
            {
                throw StoreFailure(
                    "QUERY_SAFETY_FACET_INVALID",
                    $"Safety-filtered {facetKind} facet rows are invalid or duplicated.",
                    "Rebuild the exact Query projection and safety overlay.");
            }
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<QueryListingKind, int>>
        ReadListingKindFacetCountsAsync(
            NpgsqlConnection connection,
            PublicReadRevision revision,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT document.listing_kind,
                   count(*)::integer
            FROM documents.listing_document document
            WHERE document.base_projection_id = @base_projection_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item item
                  WHERE item.overlay_id = @safety_overlay_id
                    AND item.target_kind = 'listing'
                    AND item.listing_id = document.listing_id
                    AND item.starts_at_utc <= @read_at_utc
                    AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM documents.listing_localization localization
                  JOIN projection.visibility_safety_overlay_item item
                    ON item.overlay_id = @safety_overlay_id
                   AND item.target_kind = 'route'
                   AND item.target_key = localization.route_path
                   AND item.starts_at_utc <= @read_at_utc
                   AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
                  WHERE localization.base_projection_id = document.base_projection_id
                    AND localization.listing_id = document.listing_id
              )
            GROUP BY document.listing_kind
            ORDER BY document.listing_kind;
            """;
        await using var command = CreateFacetCommand(connection, sql, revision, readAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<QueryListingKind, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = MapListingKind(reader.GetString(0));
            var count = reader.GetInt32(1);
            if (count < 1 || !result.TryAdd(kind, count))
            {
                throw StoreFailure(
                    "QUERY_SAFETY_FACET_INVALID",
                    $"Safety-filtered listing-kind facet '{kind}' is invalid or duplicated.",
                    "Rebuild the exact Query projection and safety overlay.");
            }
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<QueryContactKind, int>>
        ReadContactKindFacetCountsAsync(
            NpgsqlConnection connection,
            PublicReadRevision revision,
            DateTimeOffset readAtUtc,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT contact.kind,
                   count(DISTINCT contact.listing_id)::integer
            FROM documents.listing_contact contact
            WHERE contact.base_projection_id = @base_projection_id
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item item
                  WHERE item.overlay_id = @safety_overlay_id
                    AND item.target_kind = 'listing'
                    AND item.listing_id = contact.listing_id
                    AND item.starts_at_utc <= @read_at_utc
                    AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM documents.listing_localization localization
                  JOIN projection.visibility_safety_overlay_item item
                    ON item.overlay_id = @safety_overlay_id
                   AND item.target_kind = 'route'
                   AND item.target_key = localization.route_path
                   AND item.starts_at_utc <= @read_at_utc
                   AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
                  WHERE localization.base_projection_id = contact.base_projection_id
                    AND localization.listing_id = contact.listing_id
              )
              AND NOT EXISTS
              (
                  SELECT 1
                  FROM projection.visibility_safety_overlay_item item
                  WHERE item.overlay_id = @safety_overlay_id
                    AND item.target_kind = 'contact'
                    AND item.target_key = contact.contact_id::text
                    AND item.starts_at_utc <= @read_at_utc
                    AND (item.expires_at_utc IS NULL OR @read_at_utc < item.expires_at_utc)
              )
            GROUP BY contact.kind
            ORDER BY contact.kind;
            """;
        await using var command = CreateFacetCommand(connection, sql, revision, readAtUtc);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<QueryContactKind, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = MapContactKind(reader.GetString(0));
            var count = reader.GetInt32(1);
            if (count < 1 || !result.TryAdd(kind, count))
            {
                throw StoreFailure(
                    "QUERY_SAFETY_FACET_INVALID",
                    $"Safety-filtered contact-kind facet '{kind}' is invalid or duplicated.",
                    "Rebuild the exact Query projection and safety overlay.");
            }
        }

        return result;
    }

    private static NpgsqlCommand CreateFacetCommand(
        NpgsqlConnection connection,
        string sql,
        PublicReadRevision revision,
        DateTimeOffset readAtUtc)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "base_projection_id",
            revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "safety_overlay_id",
            revision.SafetyOverlayId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("read_at_utc", readAtUtc));
        return command;
    }

}
