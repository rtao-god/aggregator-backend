using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

/// <summary>
/// Applies one exact immutable safety overlay to all public reads and refuses traffic while any
/// Catalog suppression event is known but not yet represented by the current public-read revision.
/// </summary>
public sealed class SafetyAwarePublicQueryStore : IPublicQueryStore
{
    private const int InnerPageSize = 101;
    private readonly NpgsqlPublicQueryStore _inner;
    private readonly NpgsqlDataSource _dataSource;
    private readonly IQueryClock _clock;

    public SafetyAwarePublicQueryStore(
        NpgsqlPublicQueryStore inner,
        NpgsqlDataSource dataSource,
        IQueryClock clock)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<PublicReadPageSnapshot?> ReadPageAsync(
        string catalogKey,
        Guid? afterListingId,
        int maximumDocuments,
        string? categoryKey,
        string requestedLocale,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
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
                categoryKey,
                requestedLocale,
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
            facets);
    }

    public async Task<PublicReadDocumentSnapshot?> ReadByRouteAsync(
        string catalogKey,
        string routePath,
        CancellationToken cancellationToken)
    {
        var raw = await _inner.ReadByRouteAsync(catalogKey, routePath, cancellationToken);
        if (raw is null)
        {
            return null;
        }

        var readAtUtc = _clock.GetUtcNow();
        if (readAtUtc.Offset != TimeSpan.Zero)
        {
            throw StoreFailure(
                "QUERY_SAFETY_CLOCK_NOT_UTC",
                "Safety-aware Query store clock returned a non-UTC timestamp.",
                "Configure the Query public-read clock to return UTC timestamps.");
        }

        var safety = await LoadSafetyAsync(raw.Revision, readAtUtc, cancellationToken);
        var routeSuppression = safety.FindRouteSuppression(routePath);
        if (routeSuppression is not null)
        {
            return ApplyWholeResourceSuppression(raw, routeSuppression, routePath);
        }

        if (raw.Document is null)
        {
            return raw;
        }

        var listingSuppression = safety.FindListingSuppression(raw.Document.ListingId);
        if (listingSuppression is not null)
        {
            return ApplyWholeResourceSuppression(raw, listingSuppression, routePath);
        }

        return new PublicReadDocumentSnapshot(
            raw.Revision,
            raw.LocalePolicy,
            safety.FilterChildren(raw.Document));
    }

    private static PublicReadDocumentSnapshot ApplyWholeResourceSuppression(
        PublicReadDocumentSnapshot raw,
        QueryVisibilitySuppression suppression,
        string routePath)
    {
        return suppression.ResponseMode switch
        {
            QueryVisibilitySuppressionResponseMode.HideAsNotFound =>
                new PublicReadDocumentSnapshot(raw.Revision, raw.LocalePolicy, null),
            QueryVisibilitySuppressionResponseMode.Gone => throw SuppressedRouteFailure(
                suppression,
                routePath,
                410,
                "QUERY_ROUTE_GONE",
                "The requested public route is no longer available."),
            QueryVisibilitySuppressionResponseMode.TemporarilyUnavailable => throw SuppressedRouteFailure(
                suppression,
                routePath,
                503,
                "QUERY_ROUTE_TEMPORARILY_UNAVAILABLE",
                "The requested public route is temporarily unavailable."),
            QueryVisibilitySuppressionResponseMode.OmitChildElement => throw StoreFailure(
                "QUERY_SAFETY_RESPONSE_MODE_INVALID",
                "A listing or route suppression cannot use child-element omission.",
                "Correct the Catalog suppression response mode and rebuild the safety overlay."),
            _ => throw StoreFailure(
                "QUERY_SAFETY_RESPONSE_MODE_UNSUPPORTED",
                $"Safety overlay contains unsupported response mode '{suppression.ResponseMode}'.",
                "Restore or rebuild the safety overlay using the current owner contract."),
        };
    }

    private static QueryReadException SuppressedRouteFailure(
        QueryVisibilitySuppression suppression,
        string routePath,
        int statusCode,
        string code,
        string message) =>
        new(
            "Query.VisibilitySafety",
            code,
            statusCode,
            message,
            "Use the current Catalog route set after the suppression owner publishes a resolved state.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["routePath"] = routePath,
                ["publicReasonClass"] = suppression.PublicReasonClass,
                ["suppressionId"] = suppression.SuppressionId,
            });

    private async Task<QueryVisibilitySafetyFilter> LoadSafetyAsync(
        PublicReadRevision revision,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureCatalogNotBlockedAsync(
            connection,
            revision.CatalogKey,
            cancellationToken);

        const string overlaySql = """
            SELECT catalog_key, kind, item_count
            FROM projection.overlay_revision
            WHERE id = @overlay_id;
            """;
        await using var overlayCommand = new NpgsqlCommand(overlaySql, connection);
        overlayCommand.Parameters.Add(new NpgsqlParameter<Guid>("overlay_id", revision.SafetyOverlayId));
        await using var overlayReader = await overlayCommand.ExecuteReaderAsync(cancellationToken);
        if (!await overlayReader.ReadAsync(cancellationToken))
        {
            throw StoreFailure(
                "QUERY_SAFETY_OVERLAY_MISSING",
                $"Public-read revision '{revision.Id}' references missing safety overlay '{revision.SafetyOverlayId}'.",
                "Restore the exact Query overlay or rebuild the public-read revision.");
        }

        if (!string.Equals(overlayReader.GetString(0), revision.CatalogKey, StringComparison.Ordinal) ||
            !string.Equals(overlayReader.GetString(1), "visibility_safety", StringComparison.Ordinal))
        {
            throw StoreFailure(
                "QUERY_SAFETY_OVERLAY_IDENTITY_INVALID",
                $"Safety overlay '{revision.SafetyOverlayId}' has invalid owner identity.",
                "Restore the exact Query overlay or rebuild the public-read revision.");
        }

        var expectedItemCount = overlayReader.GetInt32(2);
        await overlayReader.DisposeAsync();

        const string itemSql = """
            SELECT suppression_id,
                   target_kind,
                   listing_id,
                   target_key,
                   public_reason_class,
                   response_mode,
                   starts_at_utc,
                   expires_at_utc,
                   aggregate_revision,
                   occurred_at_utc
            FROM projection.visibility_safety_overlay_item
            WHERE overlay_id = @overlay_id
            ORDER BY suppression_id;
            """;
        await using var itemCommand = new NpgsqlCommand(itemSql, connection);
        itemCommand.Parameters.Add(new NpgsqlParameter<Guid>("overlay_id", revision.SafetyOverlayId));
        await using var itemReader = await itemCommand.ExecuteReaderAsync(cancellationToken);
        var effectiveItems = new List<QueryVisibilitySuppression>();
        var persistedItemCount = 0;
        while (await itemReader.ReadAsync(cancellationToken))
        {
            persistedItemCount++;
            var item = QueryVisibilitySuppression.Create(
                itemReader.GetGuid(0),
                revision.CatalogKey,
                ParseTargetKind(itemReader.GetString(1)),
                itemReader.IsDBNull(2) ? null : itemReader.GetGuid(2),
                itemReader.GetString(3),
                itemReader.GetString(4),
                ParseResponseMode(itemReader.GetString(5)),
                QueryVisibilitySuppressionState.Active,
                itemReader.GetFieldValue<DateTimeOffset>(6),
                itemReader.IsDBNull(7) ? null : itemReader.GetFieldValue<DateTimeOffset>(7),
                itemReader.GetInt64(8),
                itemReader.GetFieldValue<DateTimeOffset>(9));
            if (item.IsEffectiveAt(readAtUtc))
            {
                effectiveItems.Add(item);
            }
        }

        if (persistedItemCount != expectedItemCount)
        {
            throw StoreFailure(
                "QUERY_SAFETY_OVERLAY_COUNT_INVALID",
                $"Safety overlay '{revision.SafetyOverlayId}' expected '{expectedItemCount}' items but persisted '{persistedItemCount}'.",
                "Restore or rebuild the exact safety overlay.");
        }

        return QueryVisibilitySafetyFilter.Create(effectiveItems);
    }

    private static async Task EnsureCatalogNotBlockedAsync(
        NpgsqlConnection connection,
        string catalogKey,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT source_event_id, reason_code, blocked_at_utc
            FROM projection.catalog_visibility_block
            WHERE catalog_key = @catalog_key
            ORDER BY blocked_at_utc, block_id
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<string>("catalog_key", catalogKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            throw new QueryReadException(
                "Query.VisibilitySafety",
                "QUERY_VISIBILITY_UPDATE_PENDING",
                503,
                $"Catalog '{catalogKey}' has a known visibility change that is not yet active in its public-read revision.",
                "Keep public traffic blocked until the exact safety overlay and public-read revision switch complete.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["sourceEventId"] = reader.GetGuid(0),
                    ["reasonCode"] = reader.GetString(1),
                    ["blockedAtUtc"] = reader.GetFieldValue<DateTimeOffset>(2),
                });
        }
    }

    private async Task<IReadOnlyDictionary<string, int>> ReadFacetCountsAsync(
        PublicReadRevision revision,
        DateTimeOffset readAtUtc,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureCatalogNotBlockedAsync(connection, revision.CatalogKey, cancellationToken);
        const string sql = """
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
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "base_projection_id",
            revision.BaseProjectionId));
        command.Parameters.Add(new NpgsqlParameter<Guid>(
            "safety_overlay_id",
            revision.SafetyOverlayId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("read_at_utc", readAtUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0), reader.GetInt32(1));
        }

        return result;
    }

    private static QueryVisibilitySuppressionTargetKind ParseTargetKind(string value)
    {
        return value switch
        {
            "listing" => QueryVisibilitySuppressionTargetKind.Listing,
            "media" => QueryVisibilitySuppressionTargetKind.Media,
            "contact" => QueryVisibilitySuppressionTargetKind.Contact,
            "route" => QueryVisibilitySuppressionTargetKind.Route,
            _ => throw StoreFailure(
                "QUERY_SAFETY_TARGET_KIND_UNSUPPORTED",
                $"Safety overlay contains unsupported target kind '{value}'.",
                "Restore or rebuild the safety overlay using the current owner contract."),
        };
    }

    private static QueryVisibilitySuppressionResponseMode ParseResponseMode(string value)
    {
        return value switch
        {
            "hide_as_not_found" => QueryVisibilitySuppressionResponseMode.HideAsNotFound,
            "gone" => QueryVisibilitySuppressionResponseMode.Gone,
            "temporarily_unavailable" => QueryVisibilitySuppressionResponseMode.TemporarilyUnavailable,
            "omit_child_element" => QueryVisibilitySuppressionResponseMode.OmitChildElement,
            _ => throw StoreFailure(
                "QUERY_SAFETY_RESPONSE_MODE_UNSUPPORTED",
                $"Safety overlay contains unsupported response mode '{value}'.",
                "Restore or rebuild the safety overlay using the current owner contract."),
        };
    }

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

}
