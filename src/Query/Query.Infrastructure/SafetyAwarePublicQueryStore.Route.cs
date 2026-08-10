using Aggregator.Query.Application;
using Aggregator.Query.Domain;
using Npgsql;
using NpgsqlTypes;

namespace Aggregator.Query.Infrastructure;

public sealed partial class SafetyAwarePublicQueryStore
{
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

}
