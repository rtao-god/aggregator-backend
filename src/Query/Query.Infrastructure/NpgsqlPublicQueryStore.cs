using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;

namespace Aggregator.Query.Infrastructure;

public sealed partial class NpgsqlPublicQueryStore : IPublicQueryStore
{
    private readonly NpgsqlDataSource _dataSource;

    public NpgsqlPublicQueryStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
    }

    public async Task<PublicReadPageSnapshot?> ReadPageAsync(
        string catalogKey,
        Guid? afterListingId,
        int maximumDocuments,
        PublicListingSearchCriteria criteria,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentException.ThrowIfNullOrWhiteSpace(criteria.RequestedLocale);
        if (readAtUtc.Offset != TimeSpan.Zero)
        {
            throw StoreFailure(
                "QUERY_STORE_READ_TIMESTAMP_NOT_UTC",
                "Query public store received a non-UTC read timestamp.",
                "Normalize the Query application clock to UTC before reading persistence.");
        }

        if (maximumDocuments is < 1 or > 101)
        {
            throw StoreFailure(
                "QUERY_STORE_PAGE_LIMIT_INVALID",
                "Query public store received an invalid page limit.",
                "Correct the Query application request before reading persistence.");
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var context = await ReadCurrentContextAsync(connection, catalogKey, cancellationToken);
        if (context is null)
        {
            return null;
        }

        var coreRows = await ReadCorePageAsync(
            connection,
            context.Revision.BaseProjectionId,
            afterListingId,
            maximumDocuments,
            criteria,
            cancellationToken);
        var documents = coreRows.Count == 0
            ? Array.Empty<QueryListingDocument>()
            : await LoadDocumentsAsync(
                connection,
                context.Revision.BaseProjectionId,
                coreRows,
                cancellationToken);
        var sponsored = await ReadSponsoredAsync(
            connection,
            context.Revision,
            catalogKey,
            criteria,
            readAtUtc,
            cancellationToken);
        var facets = await ReadFacetsAsync(
            connection,
            context.Revision.BaseProjectionId,
            cancellationToken);
        return new PublicReadPageSnapshot(
            context.Revision,
            context.LocalePolicy,
            documents,
            sponsored,
            facets.CategoryCounts,
            facets.DistrictCounts,
            facets.ListingKindCounts,
            facets.ContactKindCounts);
    }

    private static async Task<IReadOnlyList<PublicSponsoredListingSnapshot>> ReadSponsoredAsync(
        NpgsqlConnection connection,
        PublicReadRevision revision,
        string catalogKey,
        PublicListingSearchCriteria criteria,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT item.placement_id,
                   item.entitlement_id,
                   item.listing_id,
                   item.product_key,
                   item.scope_type,
                   item.scope_key,
                   item.locale_scope,
                   item.starts_at_utc,
                   item.ends_at_utc,
                   item.hard_expiry_at_utc,
                   item.priority_band,
                   item.capacity_slot,
                   item.presentation_label_key,
                   item.placement_revision,
                   state.state,
                   state.source_event_occurred_at_utc,
                   document.listing_id,
                   document.listing_revision_id,
                   document.subject_id,
                   document.subject_revision_id,
                   document.listing_kind,
                   document.source_content_digest,
                   document.published_at_utc
            FROM projection.promotion_overlay_item item
            LEFT JOIN projection.promotion_placement_state state
              ON state.placement_id = item.placement_id
             AND state.placement_revision = item.placement_revision
             AND state.catalog_key = @catalog_key
            LEFT JOIN documents.listing_document document
              ON document.base_projection_id = @base_projection_id
             AND document.listing_id = item.listing_id
            WHERE item.overlay_id = @promotion_overlay_id
              AND item.starts_at_utc <= @read_at_utc
              AND @read_at_utc < item.hard_expiry_at_utc
              AND @requested_locale = ANY (item.locale_scope)
              AND
              (
                  item.scope_type = 'editorial_landing'
                  OR (item.scope_type = 'catalog' AND item.scope_key = @catalog_key)
                  OR (item.scope_type = 'category' AND @category_key IS NOT NULL AND item.scope_key = @category_key)
                  OR (item.scope_type = 'district' AND @district_key IS NOT NULL AND item.scope_key = @district_key)
              )
              AND (@listing_kind IS NULL OR document.listing_kind = @listing_kind)
              AND
              (
                  @contact_kind IS NULL
                  OR EXISTS
                  (
                      SELECT 1
                      FROM documents.listing_contact contact_filter
                      WHERE contact_filter.base_projection_id = document.base_projection_id
                        AND contact_filter.listing_id = document.listing_id
                        AND contact_filter.kind = @contact_kind
                  )
              )
              AND
              (
                  @category_key IS NULL
                  OR EXISTS
                  (
                      SELECT 1
                      FROM documents.listing_category category_filter
                      WHERE category_filter.base_projection_id = document.base_projection_id
                        AND category_filter.listing_id = document.listing_id
                        AND category_filter.category_key = @category_key
                  )
              )
              AND
              (
                  @district_key IS NULL
                  OR EXISTS
                  (
                      SELECT 1
                      FROM documents.listing_geography district_filter
                      WHERE district_filter.base_projection_id = document.base_projection_id
                        AND district_filter.listing_id = document.listing_id
                        AND district_filter.district_key = @district_key
                  )
              )
              AND
              (
                  @market_zone IS NULL
                  OR EXISTS
                  (
                      SELECT 1
                      FROM documents.listing_geography market_zone_filter
                      WHERE market_zone_filter.base_projection_id = document.base_projection_id
                        AND market_zone_filter.listing_id = document.listing_id
                        AND market_zone_filter.state = @market_zone
                  )
              )
            ORDER BY item.priority_band DESC,
                     item.capacity_slot,
                     item.placement_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("promotion_overlay_id", revision.PromotionOverlayId));
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("read_at_utc", readAtUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("requested_locale", criteria.RequestedLocale));
        command.Parameters.Add(new NpgsqlParameter<string?>("category_key", criteria.CategoryKey));
        command.Parameters.Add(new NpgsqlParameter<string?>("district_key", criteria.DistrictKey));
        command.Parameters.Add(new NpgsqlParameter<string?>(
            "listing_kind",
            criteria.ListingKind is null ? null : ToPersistedListingKind(criteria.ListingKind.Value)));
        command.Parameters.Add(new NpgsqlParameter<string?>(
            "contact_kind",
            criteria.ContactKind is null ? null : ToPersistedContactKind(criteria.ContactKind.Value)));
        command.Parameters.Add(new NpgsqlParameter<string?>(
            "market_zone",
            criteria.MarketZone is null ? null : ToPersistedGeographyState(criteria.MarketZone.Value)));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<SponsoredCoreRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(14) || reader.IsDBNull(16))
            {
                throw StoreFailure(
                    "QUERY_SPONSORED_REFERENCE_MISSING",
                    "Active Promotion overlay references missing current placement or base listing state.",
                    "Replay the exact Promotion event or rebuild Query against the sealed Catalog publication.");
            }

            var placement = QueryPromotionPlacement.Create(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                catalogKey,
                reader.GetString(3),
                MapPromotionScope(reader.GetString(4)),
                reader.GetString(5),
                reader.GetFieldValue<string[]>(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetInt32(10),
                reader.GetInt32(11),
                reader.GetString(12),
                MapPromotionState(reader.GetString(14)),
                reader.GetInt64(13),
                reader.GetFieldValue<DateTimeOffset>(15));
            var core = new ListingCoreRow(
                reader.GetGuid(16),
                reader.GetGuid(17),
                reader.GetGuid(18),
                reader.GetGuid(19),
                MapListingKind(reader.GetString(20)),
                reader.GetString(21).TrimEnd(),
                reader.GetFieldValue<DateTimeOffset>(22));
            rows.Add(new SponsoredCoreRow(placement, core));
        }

        if (rows.Count == 0)
        {
            return Array.Empty<PublicSponsoredListingSnapshot>();
        }

        var distinctCoreRows = rows
            .Select(item => item.Document)
            .DistinctBy(item => item.ListingId)
            .ToArray();
        var documents = await LoadDocumentsAsync(
            connection,
            revision.BaseProjectionId,
            distinctCoreRows,
            cancellationToken);
        var documentsById = documents.ToDictionary(item => item.ListingId);
        return rows
            .Select(item => new PublicSponsoredListingSnapshot(
                item.Placement,
                documentsById[item.Document.ListingId]))
            .ToArray();
    }

    public async Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
        string catalogKey,
        string routePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(routePath);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var context = await ReadCurrentContextAsync(connection, catalogKey, cancellationToken);
        if (context is null)
        {
            return null;
        }

        const string listingSql = """
            SELECT d.listing_id,
                   d.listing_revision_id,
                   d.subject_id,
                   d.subject_revision_id,
                   d.listing_kind,
                   d.source_content_digest,
                   d.published_at_utc
            FROM documents.listing_localization l
            JOIN documents.listing_document d
              ON d.base_projection_id = l.base_projection_id
             AND d.listing_id = l.listing_id
            WHERE l.base_projection_id = @base_projection_id
              AND l.route_path = @route_path;
            """;
        await using var command = new NpgsqlCommand(listingSql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", context.Revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<string>("route_path", routePath));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new PublicReadDocumentSnapshot(context.Revision, context.LocalePolicy, null);
        }

        var core = ReadCoreRow(reader);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw StoreFailure(
                "QUERY_ROUTE_NOT_UNIQUE",
                $"Route '{routePath}' resolves to more than one listing in the active base projection.",
                "Rebuild Query from a Catalog publication with unique routes.");
        }

        await reader.DisposeAsync();
        var documents = await LoadDocumentsAsync(
            connection,
            context.Revision.BaseProjectionId,
            [core],
            cancellationToken);
        return new PublicReadDocumentSnapshot(
            context.Revision,
            context.LocalePolicy,
            documents.Single());
    }

    private static async Task<PublicReadContext?> ReadCurrentContextAsync(
        NpgsqlConnection connection,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.id,
                   r.catalog_key,
                   r.base_projection_id,
                   r.promotion_overlay_id,
                   r.safety_overlay_id,
                   r.source_publication_id,
                   r.created_at_utc,
                   r.content_digest,
                   b.default_locale,
                   b.supported_locales
            FROM projection.current_public_read p
            JOIN projection.public_read_revision r
              ON r.id = p.public_read_revision_id
            JOIN projection.base_projection b
              ON b.id = r.base_projection_id
            WHERE p.catalog_key = @catalog_key;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var revision = PublicReadRevision.Restore(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7).TrimEnd());
        var localePolicy = QueryLocalePolicy.Create(
            reader.GetString(8),
            reader.GetFieldValue<string[]>(9));
        return new PublicReadContext(revision, localePolicy);
    }

    private static async Task<IReadOnlyList<ListingCoreRow>> ReadCorePageAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        Guid? afterListingId,
        int maximumDocuments,
        PublicListingSearchCriteria criteria,
        CancellationToken cancellationToken)
    {
        var afterClause = afterListingId is null
            ? string.Empty
            : "AND d.listing_id > @after_listing_id";
        var categoryClause = criteria.CategoryKey is null
            ? string.Empty
            : """
              AND EXISTS
              (
                  SELECT 1
                  FROM documents.listing_category category_filter
                  WHERE category_filter.base_projection_id = d.base_projection_id
                    AND category_filter.listing_id = d.listing_id
                    AND category_filter.category_key = @category_key
              )
              """;
        var districtClause = criteria.DistrictKey is null
            ? string.Empty
            : """
              AND EXISTS
              (
                  SELECT 1
                  FROM documents.listing_geography district_filter
                  WHERE district_filter.base_projection_id = d.base_projection_id
                    AND district_filter.listing_id = d.listing_id
                    AND district_filter.district_key = @district_key
              )
              """;
        var listingKindClause = criteria.ListingKind is null
            ? string.Empty
            : "AND d.listing_kind = @listing_kind";
        var contactKindClause = criteria.ContactKind is null
            ? string.Empty
            : """
              AND EXISTS
              (
                  SELECT 1
                  FROM documents.listing_contact contact_filter
                  WHERE contact_filter.base_projection_id = d.base_projection_id
                    AND contact_filter.listing_id = d.listing_id
                    AND contact_filter.kind = @contact_kind
              )
              """;
        var marketZoneClause = criteria.MarketZone is null
            ? string.Empty
            : """
              AND EXISTS
              (
                  SELECT 1
                  FROM documents.listing_geography market_zone_filter
                  WHERE market_zone_filter.base_projection_id = d.base_projection_id
                    AND market_zone_filter.listing_id = d.listing_id
                    AND market_zone_filter.state = @market_zone
              )
              """;
        var sql = $"""
            SELECT d.listing_id,
                   d.listing_revision_id,
                   d.subject_id,
                   d.subject_revision_id,
                   d.listing_kind,
                   d.source_content_digest,
                   d.published_at_utc
            FROM documents.listing_document d
            WHERE d.base_projection_id = @base_projection_id
            {afterClause}
            {categoryClause}
            {districtClause}
            {listingKindClause}
            {contactKindClause}
            {marketZoneClause}
            ORDER BY d.listing_id
            LIMIT @maximum_documents;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<int>("maximum_documents", maximumDocuments));
        if (afterListingId is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("after_listing_id", afterListingId.Value));
        }

        if (criteria.CategoryKey is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string>("category_key", criteria.CategoryKey));
        }

        if (criteria.DistrictKey is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string>("district_key", criteria.DistrictKey));
        }

        if (criteria.ListingKind is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string>(
                "listing_kind",
                ToPersistedListingKind(criteria.ListingKind.Value)));
        }

        if (criteria.ContactKind is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string>(
                "contact_kind",
                ToPersistedContactKind(criteria.ContactKind.Value)));
        }

        if (criteria.MarketZone is not null)
        {
            command.Parameters.Add(new NpgsqlParameter<string>(
                "market_zone",
                ToPersistedGeographyState(criteria.MarketZone.Value)));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ListingCoreRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadCoreRow(reader));
        }

        return rows;
    }

    private static ListingCoreRow ReadCoreRow(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            MapListingKind(reader.GetString(4)),
            reader.GetString(5).TrimEnd(),
            reader.GetFieldValue<DateTimeOffset>(6));

    private static async Task<IReadOnlyList<QueryListingDocument>> LoadDocumentsAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        IReadOnlyList<ListingCoreRow> coreRows,
        CancellationToken cancellationToken)
    {
        var listingIds = coreRows.Select(item => item.ListingId).ToArray();
        var localizations = await ReadLocalizationsAsync(connection, baseProjectionId, listingIds, cancellationToken);
        var categories = await ReadCategoriesAsync(connection, baseProjectionId, listingIds, cancellationToken);
        var attributes = await ReadAttributesAsync(connection, baseProjectionId, listingIds, cancellationToken);
        var geographies = await ReadGeographiesAsync(connection, baseProjectionId, listingIds, cancellationToken);
        var contacts = await ReadContactsAsync(connection, baseProjectionId, listingIds, cancellationToken);
        var media = await ReadMediaAsync(connection, baseProjectionId, listingIds, cancellationToken);
        return coreRows
            .Select(row => QueryListingDocument.Create(
                row.ListingId,
                row.ListingRevisionId,
                row.SubjectId,
                row.SubjectRevisionId,
                row.ListingKind,
                RequireOwned(localizations, row.ListingId, "localization"),
                RequireOwned(categories, row.ListingId, "category"),
                RequireOwned(attributes, row.ListingId, "attribute"),
                RequireGeography(geographies, row.ListingId),
                RequireOwned(contacts, row.ListingId, "contact"),
                RequireOwned(media, row.ListingId, "media"),
                row.SourceContentDigest,
                row.PublishedAtUtc))
            .ToArray();
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<QueryLocalizedDocument>>> ReadLocalizationsAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        Guid[] listingIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT listing_id,
                   locale,
                   route_path,
                   title,
                   description_state,
                   description
            FROM documents.listing_localization
            WHERE base_projection_id = @base_projection_id
              AND listing_id = ANY (@listing_ids)
            ORDER BY listing_id, locale;
            """;
        await using var command = CreateOwnedRowsCommand(connection, sql, baseProjectionId, listingIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new Dictionary<Guid, List<QueryLocalizedDocument>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            Add(values, reader.GetGuid(0), new QueryLocalizedDocument(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                MapFieldState(reader.GetString(4)),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return ToReadOnly(values);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<string>>> ReadCategoriesAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        Guid[] listingIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT listing_id, category_key
            FROM documents.listing_category
            WHERE base_projection_id = @base_projection_id
              AND listing_id = ANY (@listing_ids)
            ORDER BY listing_id, category_key;
            """;
        await using var command = CreateOwnedRowsCommand(connection, sql, baseProjectionId, listingIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new Dictionary<Guid, List<string>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            Add(values, reader.GetGuid(0), reader.GetString(1));
        }

        return ToReadOnly(values);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<QueryAttributeDocument>>> ReadAttributesAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        Guid[] listingIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT listing_id,
                   attribute_key,
                   state,
                   value_kind,
                   boolean_value,
                   decimal_value,
                   text_value,
                   text_collection_value
            FROM documents.listing_attribute
            WHERE base_projection_id = @base_projection_id
              AND listing_id = ANY (@listing_ids)
            ORDER BY listing_id, attribute_key;
            """;
        await using var command = CreateOwnedRowsCommand(connection, sql, baseProjectionId, listingIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new Dictionary<Guid, List<QueryAttributeDocument>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var state = MapFieldState(reader.GetString(2));
            var valueKind = reader.IsDBNull(3)
                ? (QueryValueKind?)null
                : MapValueKind(reader.GetString(3));
            var textCollection = reader.IsDBNull(7)
                ? null
                : reader.GetFieldValue<string[]>(7);
            Add(values, reader.GetGuid(0), new QueryAttributeDocument(
                reader.GetString(1),
                state,
                valueKind,
                reader.IsDBNull(4) ? null : reader.GetBoolean(4),
                reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                textCollection));
        }

        return ToReadOnly(values);
    }

    private static async Task<Dictionary<Guid, QueryGeographyDocument>> ReadGeographiesAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        Guid[] listingIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT listing_id,
                   state,
                   latitude,
                   longitude,
                   district_key
            FROM documents.listing_geography
            WHERE base_projection_id = @base_projection_id
              AND listing_id = ANY (@listing_ids);
            """;
        await using var command = CreateOwnedRowsCommand(connection, sql, baseProjectionId, listingIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new Dictionary<Guid, QueryGeographyDocument>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var listingId = reader.GetGuid(0);
            if (!values.TryAdd(listingId, new QueryGeographyDocument(
                    MapGeographyState(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                    reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4))))
            {
                throw StoreFailure(
                    "QUERY_DOCUMENT_COMPONENT_DUPLICATE",
                    $"Listing '{listingId}' has more than one persisted geography row.",
                    "Rebuild the Query projection from the sealed Catalog publication.");
            }
        }

        return values;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<QueryContactDocument>>> ReadContactsAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        Guid[] listingIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT listing_id,
                   contact_id,
                   ordinal,
                   kind,
                   target,
                   label
            FROM documents.listing_contact
            WHERE base_projection_id = @base_projection_id
              AND listing_id = ANY (@listing_ids)
            ORDER BY listing_id, ordinal;
            """;
        await using var command = CreateOwnedRowsCommand(connection, sql, baseProjectionId, listingIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new Dictionary<Guid, List<QueryContactDocument>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            Add(values, reader.GetGuid(0), new QueryContactDocument(
                reader.GetGuid(1),
                MapContactKind(reader.GetString(3)),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return ToReadOnly(values);
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<QueryMediaDocument>>> ReadMediaAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        Guid[] listingIds,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT listing_id,
                   media_id,
                   object_uri,
                   content_type,
                   content_digest,
                   rights_basis
            FROM documents.listing_media
            WHERE base_projection_id = @base_projection_id
              AND listing_id = ANY (@listing_ids)
            ORDER BY listing_id, media_id;
            """;
        await using var command = CreateOwnedRowsCommand(connection, sql, baseProjectionId, listingIds);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new Dictionary<Guid, List<QueryMediaDocument>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            Add(values, reader.GetGuid(0), new QueryMediaDocument(
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4).TrimEnd(),
                MapRightsBasis(reader.GetString(5))));
        }

        return ToReadOnly(values);
    }

    private static async Task<PublicFacetSnapshot> ReadFacetsAsync(
        NpgsqlConnection connection,
        Guid baseProjectionId,
        CancellationToken cancellationToken)
    {
        var categories = await ReadStringFacetsAsync(
            connection,
            """
            SELECT category_key, listing_count
            FROM documents.category_facet
            WHERE base_projection_id = @base_projection_id
            ORDER BY category_key;
            """,
            baseProjectionId,
            "category",
            cancellationToken);
        var districts = await ReadStringFacetsAsync(
            connection,
            """
            SELECT district_key, COUNT(*)::integer
            FROM documents.listing_geography
            WHERE base_projection_id = @base_projection_id
              AND district_key IS NOT NULL
            GROUP BY district_key
            ORDER BY district_key;
            """,
            baseProjectionId,
            "district",
            cancellationToken);
        var listingKinds = await ReadListingKindFacetsAsync(
            connection,
            baseProjectionId,
            cancellationToken);
        var contactKinds = await ReadContactKindFacetsAsync(
            connection,
            baseProjectionId,
            cancellationToken);
        return new PublicFacetSnapshot(
            categories,
            districts,
            listingKinds,
            contactKinds);
    }

    private static async Task<IReadOnlyDictionary<string, int>> ReadStringFacetsAsync(
        NpgsqlConnection connection,
        string sql,
        Guid baseProjectionId,
        string facetKind,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!result.TryAdd(reader.GetString(0), reader.GetInt32(1)))
            {
                throw StoreFailure(
                    "QUERY_FACET_DUPLICATE",
                    $"Query persistence contains a duplicate {facetKind} facet row.",
                    "Rebuild the Query projection from the sealed Catalog publication.");
            }
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<QueryListingKind, int>>
        ReadListingKindFacetsAsync(
            NpgsqlConnection connection,
            Guid baseProjectionId,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT listing_kind, COUNT(*)::integer
            FROM documents.listing_document
            WHERE base_projection_id = @base_projection_id
            GROUP BY listing_kind
            ORDER BY listing_kind;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<QueryListingKind, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = MapListingKind(reader.GetString(0));
            if (!result.TryAdd(kind, reader.GetInt32(1)))
            {
                throw StoreFailure(
                    "QUERY_FACET_DUPLICATE",
                    $"Query persistence contains a duplicate listing-kind facet '{kind}'.",
                    "Rebuild the Query projection from the sealed Catalog publication.");
            }
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<QueryContactKind, int>>
        ReadContactKindFacetsAsync(
            NpgsqlConnection connection,
            Guid baseProjectionId,
            CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT kind, COUNT(DISTINCT listing_id)::integer
            FROM documents.listing_contact
            WHERE base_projection_id = @base_projection_id
            GROUP BY kind
            ORDER BY kind;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<QueryContactKind, int>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var kind = MapContactKind(reader.GetString(0));
            if (!result.TryAdd(kind, reader.GetInt32(1)))
            {
                throw StoreFailure(
                    "QUERY_FACET_DUPLICATE",
                    $"Query persistence contains a duplicate contact-kind facet '{kind}'.",
                    "Rebuild the Query projection from the sealed Catalog publication.");
            }
        }

        return result;
    }

    private static NpgsqlCommand CreateOwnedRowsCommand(
        NpgsqlConnection connection,
        string sql,
        Guid baseProjectionId,
        Guid[] listingIds)
    {
        var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>("base_projection_id", baseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("listing_ids", listingIds));
        return command;
    }

    private static void Add<T>(Dictionary<Guid, List<T>> values, Guid listingId, T value)
    {
        if (!values.TryGetValue(listingId, out var owned))
        {
            owned = [];
            values.Add(listingId, owned);
        }

        owned.Add(value);
    }

    private static Dictionary<Guid, IReadOnlyList<T>> ToReadOnly<T>(
        Dictionary<Guid, List<T>> values) =>
        values.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<T>)item.Value.ToArray());

    private static IReadOnlyList<T> RequireOwned<T>(
        Dictionary<Guid, IReadOnlyList<T>> values,
        Guid listingId,
        string valueKind)
    {
        if (!values.TryGetValue(listingId, out var owned))
        {
            throw StoreFailure(
                "QUERY_DOCUMENT_COMPONENT_MISSING",
                $"Listing '{listingId}' has no persisted {valueKind} rows.",
                "Rebuild the Query projection from the sealed Catalog publication.");
        }

        return owned;
    }

    private static QueryGeographyDocument RequireGeography(
        Dictionary<Guid, QueryGeographyDocument> values,
        Guid listingId)
    {
        if (!values.TryGetValue(listingId, out var owned))
        {
            throw StoreFailure(
                "QUERY_DOCUMENT_COMPONENT_MISSING",
                $"Listing '{listingId}' has no persisted geography row.",
                "Rebuild the Query projection from the sealed Catalog publication.");
        }

        return owned;
    }

    private static QueryListingKind MapListingKind(string value) => value switch
    {
        "place" => QueryListingKind.Place,
        "provider" => QueryListingKind.Provider,
        _ => throw UnsupportedValue("listing kind", value),
    };

    private static string ToPersistedListingKind(QueryListingKind value) => value switch
    {
        QueryListingKind.Place => "place",
        QueryListingKind.Provider => "provider",
        _ => throw UnsupportedValue("listing kind", value.ToString()),
    };

    private static string ToPersistedContactKind(QueryContactKind value) => value switch
    {
        QueryContactKind.Website => "website",
        QueryContactKind.Email => "email",
        QueryContactKind.Phone => "phone",
        QueryContactKind.WhatsApp => "whatsapp",
        QueryContactKind.BookingReference => "booking_reference",
        QueryContactKind.MapReference => "map_reference",
        _ => throw UnsupportedValue("contact kind", value.ToString()),
    };

    private static string ToPersistedGeographyState(QueryGeographyState value) => value switch
    {
        QueryGeographyState.PrimaryMarket => "primary_market",
        QueryGeographyState.NearbyMarket => "nearby_market",
        QueryGeographyState.RemoteOnly => "remote_only",
        QueryGeographyState.OutsideMarket => "outside_market",
        _ => throw UnsupportedValue("geography state", value.ToString()),
    };

    private static QueryFieldState MapFieldState(string value) => value switch
    {
        "observed" => QueryFieldState.Observed,
        "missing" => QueryFieldState.Missing,
        "not_applicable" => QueryFieldState.NotApplicable,
        "withheld" => QueryFieldState.Withheld,
        _ => throw UnsupportedValue("field state", value),
    };

    private static QueryValueKind MapValueKind(string value) => value switch
    {
        "boolean" => QueryValueKind.BooleanValue,
        "decimal" => QueryValueKind.DecimalNumber,
        "text" => QueryValueKind.TextValue,
        "text_collection" => QueryValueKind.TextCollection,
        "duration_minutes" => QueryValueKind.DurationMinutes,
        _ => throw UnsupportedValue("attribute value kind", value),
    };

    internal static QueryGeographyState MapGeographyState(string value) => value switch
    {
        "primary_market" => QueryGeographyState.PrimaryMarket,
        "nearby_market" => QueryGeographyState.NearbyMarket,
        "remote_only" => QueryGeographyState.RemoteOnly,
        "outside_market" => QueryGeographyState.OutsideMarket,
        _ => throw UnsupportedValue("geography state", value),
    };

    private static QueryPromotionPlacementScope MapPromotionScope(string value) => value switch
    {
        "catalog" => QueryPromotionPlacementScope.Catalog,
        "category" => QueryPromotionPlacementScope.Category,
        "district" => QueryPromotionPlacementScope.District,
        "editorial_landing" => QueryPromotionPlacementScope.EditorialLanding,
        _ => throw UnsupportedValue("promotion placement scope", value),
    };

    private static QueryPromotionPlacementState MapPromotionState(string value) => value switch
    {
        "scheduled" => QueryPromotionPlacementState.Scheduled,
        "active" => QueryPromotionPlacementState.Active,
        "paused" => QueryPromotionPlacementState.Paused,
        "ended" => QueryPromotionPlacementState.Ended,
        "revoked" => QueryPromotionPlacementState.Revoked,
        _ => throw UnsupportedValue("promotion placement state", value),
    };

    private static QueryContactKind MapContactKind(string value) => value switch
    {
        "website" => QueryContactKind.Website,
        "email" => QueryContactKind.Email,
        "phone" => QueryContactKind.Phone,
        "whatsapp" => QueryContactKind.WhatsApp,
        "booking_reference" => QueryContactKind.BookingReference,
        "map_reference" => QueryContactKind.MapReference,
        _ => throw UnsupportedValue("contact kind", value),
    };

    private static QueryMediaRightsBasis MapRightsBasis(string value) => value switch
    {
        "owner_provided" => QueryMediaRightsBasis.OwnerProvided,
        "explicit_license" => QueryMediaRightsBasis.ExplicitLicense,
        "original_editorial_work" => QueryMediaRightsBasis.OriginalEditorialWork,
        "public_domain" => QueryMediaRightsBasis.PublicDomain,
        _ => throw UnsupportedValue("media rights basis", value),
    };

    private static QueryReadException UnsupportedValue(string valueKind, string value) =>
        StoreFailure(
            "QUERY_STORE_VALUE_UNSUPPORTED",
            $"Query persistence contains unsupported {valueKind} '{value}'.",
            "Restore or rebuild the Query projection using the current owner contract.");

    private static QueryReadException StoreFailure(
        string code,
        string message,
        string requiredAction) =>
        new(
            "Query.Persistence",
            code,
            500,
            message,
            requiredAction);

    private sealed record PublicFacetSnapshot(
        IReadOnlyDictionary<string, int> CategoryCounts,
        IReadOnlyDictionary<string, int> DistrictCounts,
        IReadOnlyDictionary<QueryListingKind, int> ListingKindCounts,
        IReadOnlyDictionary<QueryContactKind, int> ContactKindCounts);

    private sealed record PublicReadContext(
        PublicReadRevision Revision,
        QueryLocalePolicy LocalePolicy);

    private sealed record SponsoredCoreRow(
        QueryPromotionPlacement Placement,
        ListingCoreRow Document);

    private sealed record ListingCoreRow(
        Guid ListingId,
        Guid ListingRevisionId,
        Guid SubjectId,
        Guid SubjectRevisionId,
        QueryListingKind ListingKind,
        string SourceContentDigest,
        DateTimeOffset PublishedAtUtc);
}
